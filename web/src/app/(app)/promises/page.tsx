"use client";
import { CalendarClock, Lock, Plus } from "lucide-react";
import Link from "next/link";
import { useState } from "react";
import { useMe } from "@/components/me-context";
import { Badge, Button, EmptyState, EntityPicker, ErrorMessage, Field, Leaf, Pager, Select, Skeleton, SuccessMessage, type Tone } from "@/components/ui";
import {
  api, canReadFinance, canWritePayments, formatDate, formatMoney, promiseStatusLabels, todayIso,
  type Charge, type Paged, type PaymentPromise, type PromiseStatus, type Tenant,
} from "@/lib/api";
import { useQuery } from "@/lib/use-query";

const tone: Record<PromiseStatus, Tone> = { Pending: "pending", Kept: "ledger", Broken: "stamp", Cancelled: "neutral" };
const PAGE_SIZE = 15;
const searchTenants = (term: string) => api<Paged<Tenant>>(`/tenants?status=Active&pageSize=8&search=${encodeURIComponent(term)}`).then((r) => r.items);
const addDays = (iso: string, days: number) => { const d = new Date(`${iso}T00:00:00`); d.setDate(d.getDate() + days); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`; };

export default function PromisesPage() {
  const me = useMe();
  const currency = me.organizations.find((o) => o.id === me.activeOrganizationId)?.currency ?? "MXN";
  const canWrite = canWritePayments(me.activeRole);
  const [status, setStatus] = useState<PromiseStatus | "">("Pending");
  const [page, setPage] = useState(1);
  const [version, setVersion] = useState(0);
  const [creating, setCreating] = useState(false);
  const [cancelling, setCancelling] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const qs = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE), ...(status ? { status } : {}) }).toString();
  const { data, error: loadError, loading } = useQuery(`promises:${qs}:${version}`, () => api<Paged<PaymentPromise>>(`/payment-promises?${qs}`));

  if (!canReadFinance(me.activeRole)) {
    return <Leaf><EmptyState icon={<Lock size={18} />} title="No tienes permiso para ver promesas de pago">La información financiera no está disponible para tu rol.</EmptyState></Leaf>;
  }

  async function cancel(id: string, reason: string) {
    setBusy(true); setError(null); setNotice(null);
    try { await api(`/payment-promises/${id}/cancel`, { body: { reason } }); setNotice("Promesa cancelada."); setCancelling(null); setVersion((v) => v + 1); }
    catch (e) { setError(e instanceof Error ? e.message : "No se pudo cancelar."); }
    finally { setBusy(false); }
  }

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Promesas de pago</h1>
          <p className="mt-1 text-sm text-ink-soft">La fecha en que un inquilino dijo que pagaría un cargo. Se marca cumplida o incumplida según el dinero recibido.</p>
        </div>
        <div className="flex items-end gap-3">
          <div className="w-40"><Select label="Estado" value={status} onChange={(e) => { setStatus(e.target.value as PromiseStatus | ""); setPage(1); }}>
            <option value="Pending">Pendientes</option><option value="Broken">Incumplidas</option><option value="Kept">Cumplidas</option><option value="Cancelled">Canceladas</option><option value="">Todas</option>
          </Select></div>
          {canWrite && <Button onClick={() => { setCreating((v) => !v); setNotice(null); }}><Plus size={15} aria-hidden /> Nueva promesa</Button>}
        </div>
      </div>
      <SuccessMessage message={notice} />
      <ErrorMessage message={error ?? loadError?.message ?? null} />
      {creating && <NewPromise currency={currency} onCancel={() => setCreating(false)} onCreated={() => { setCreating(false); setNotice("Promesa registrada."); setStatus("Pending"); setPage(1); setVersion((v) => v + 1); }} />}

      <Leaf className="overflow-hidden">
        {!data && !loadError && <div className="flex flex-col gap-3 p-5">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-14" />)}</div>}
        {data?.items.length === 0 && <EmptyState icon={<CalendarClock size={18} />} title="No hay promesas con ese estado">Registra una cuando un inquilino te diga cuándo va a pagar.</EmptyState>}
        {data && data.items.length > 0 && (
          <ul className={loading ? "opacity-60" : ""}>
            {data.items.map((p) => (
              <li key={p.id} className="border-b border-rule last:border-b-0">
                <div className="grid items-center gap-x-4 gap-y-1 px-5 py-3.5 md:grid-cols-[minmax(0,1.2fr)_minmax(0,1fr)_130px_110px_120px]">
                  <Link href={`/tenants/${p.tenantId}`} className="truncate text-sm font-medium hover:text-accent hover:underline">{p.tenantName}</Link>
                  <p className="min-w-0 truncate text-sm text-ink-soft">{p.chargeDescription} · vence {formatDate(p.dueDate)}</p>
                  <p className="text-sm md:text-right"><span className="text-ink-soft md:hidden">Saldo: </span>{formatMoney(p.chargeBalance, currency)}</p>
                  <p className="text-sm"><span className="text-ink-soft md:hidden">Prometió: </span><span className="font-medium">{formatDate(p.promisedOn)}</span></p>
                  <div className="flex items-center justify-between gap-2">
                    <Badge tone={tone[p.status]}>{promiseStatusLabels[p.status]}</Badge>
                    {canWrite && p.status === "Pending" && <button className="text-xs text-ink-soft underline hover:text-stamp" onClick={() => setCancelling(cancelling === p.id ? null : p.id)}>Cancelar</button>}
                  </div>
                </div>
                {p.note && <p className="px-5 pb-3 text-xs text-ink-soft">«{p.note}»</p>}
                {p.status === "Cancelled" && p.cancelReason && <p className="border-t border-rule bg-paper px-5 py-2 text-xs text-ink-soft">Cancelada: {p.cancelReason}</p>}
                {cancelling === p.id && (
                  <form noValidate className="grid items-end gap-3 border-t border-rule bg-paper px-5 py-4 sm:grid-cols-[minmax(0,1fr)_auto]" onSubmit={(e) => { e.preventDefault(); cancel(p.id, String(new FormData(e.currentTarget).get("reason") ?? "")); }}>
                    <Field label="Motivo (opcional)" name="reason" maxLength={500} />
                    <div className="flex gap-2"><Button type="button" variant="ghost" onClick={() => setCancelling(null)}>Cerrar</Button><Button type="submit" variant="danger" loading={busy}>Cancelar promesa</Button></div>
                  </form>
                )}
              </li>
            ))}
          </ul>
        )}
        {data && <Pager page={data.page} pageSize={data.pageSize} total={data.total} onPage={setPage} />}
      </Leaf>
    </div>
  );
}

function NewPromise({ currency, onCancel, onCreated }: { currency: string; onCancel: () => void; onCreated: () => void }) {
  const [tenant, setTenant] = useState<Tenant | null>(null);
  const [chargeId, setChargeId] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const charges = useQuery(`promise-charges:${tenant?.id ?? ""}`, () => (tenant ? api<Paged<Charge>>(`/rent-charges?tenantId=${tenant.id}&openOnly=true&pageSize=100`) : Promise.resolve(null)));

  async function submit(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    if (!chargeId) { setError("Elige el cargo que se va a pagar."); return; }
    setBusy(true); setError(null);
    try { await api("/payment-promises", { body: { chargeId, promisedOn: f.get("promisedOn"), note: f.get("note") || null } }); onCreated(); }
    catch (err) { setError(err instanceof Error ? err.message : "No se pudo registrar la promesa."); setBusy(false); }
  }

  return (
    <Leaf>
      <header className="px-5 pb-4 pt-5"><h2 className="text-[15px] font-semibold">Nueva promesa</h2><p className="mt-0.5 text-sm text-ink-soft">Si el cargo ya tenía una promesa abierta, esta la reemplaza.</p></header>
      <div className="perforation" />
      <form onSubmit={submit} noValidate className="flex flex-col gap-4 px-5 pb-5 pt-4">
        <EntityPicker label="Inquilino" placeholder="Buscar por nombre, correo o teléfono" selected={tenant} onSelect={(t) => { setTenant(t); setChargeId(""); }} search={searchTenants} itemKey={(t) => t.id} renderItem={(t) => <span>{t.fullName}</span>} />
        {tenant && (
          <Select label="Cargo" value={chargeId} onChange={(e) => setChargeId(e.target.value)} disabled={!charges.data}>
            <option value="">{!charges.data ? "Cargando…" : charges.data.items.length === 0 ? "Sin cargos pendientes" : "Elige un cargo"}</option>
            {charges.data?.items.map((c) => <option key={c.id} value={c.id}>{c.description} · saldo {formatMoney(c.balance, currency)} · vence {formatDate(c.dueDate)}</option>)}
          </Select>
        )}
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Fecha prometida" name="promisedOn" type="date" defaultValue={addDays(todayIso(), 3)} min={todayIso()} max={addDays(todayIso(), 90)} required />
          <Field label="Nota" name="note" maxLength={500} hint="Opcional." />
        </div>
        <ErrorMessage message={error} />
        <div className="flex gap-2"><Button type="submit" loading={busy}>Registrar promesa</Button><Button type="button" variant="ghost" onClick={onCancel} disabled={busy}>Cancelar</Button></div>
      </form>
    </Leaf>
  );
}
