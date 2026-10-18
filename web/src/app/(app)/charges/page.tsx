"use client";
import { Ban, CalendarClock, Plus, Receipt } from "lucide-react";
import Link from "next/link";
import { useEffect, useState } from "react";
import { useMe } from "@/components/me-context";
import { Badge, Button, EmptyState, EntityPicker, ErrorMessage, Field, Leaf, Pager, SearchInput, Select, Skeleton, SuccessMessage } from "@/components/ui";
import {
  api, canManage, canReadFinance, canWriteFinance, chargeStatusLabels, currentMonthValue, formatDate, formatMoney, formatMonth, parseMonthValue,
  type Charge, type ChargeStatus, type GenerationPreview, type GenerationResult, type Lease, type Paged,
} from "@/lib/api";
import { chargeTone } from "@/lib/charge-ui";
import { useQuery } from "@/lib/use-query";

const PAGE_SIZE = 25;
const searchLeases = (term: string) => api<Paged<Lease>>(`/leases?status=Active&pageSize=8&search=${encodeURIComponent(term)}`).then((r) => r.items);
const cols = "md:grid-cols-[minmax(0,1.4fr)_minmax(0,1.2fr)_110px_110px_110px_120px]";

export default function ChargesPage() {
  const me = useMe();
  const canWrite = canWriteFinance(me.activeRole);
  const canVoid = canManage(me.activeRole);
  const currency = me.organizations.find((o) => o.id === me.activeOrganizationId)?.currency ?? "MXN";

  const [version, setVersion] = useState(0);
  const [search, setSearch] = useState("");
  const [debounced, setDebounced] = useState("");
  const [status, setStatus] = useState<"" | ChargeStatus>("");
  const [month, setMonth] = useState("");
  const [page, setPage] = useState(1);
  const [panel, setPanel] = useState<"generate" | "manual" | null>(null);
  const [voiding, setVoiding] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  // generate panel
  const [genMonth, setGenMonth] = useState(currentMonthValue());
  const [preview, setPreview] = useState<GenerationPreview | null>(null);
  // manual panel
  const [lease, setLease] = useState<Lease | null>(null);

  useEffect(() => {
    const t = setTimeout(() => { setDebounced(search); setPage(1); }, 250);
    return () => clearTimeout(t);
  }, [search]);

  const params = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
  if (debounced) params.set("search", debounced);
  if (status) params.set("status", status);
  const period = parseMonthValue(month);
  if (period) { params.set("year", String(period.year)); params.set("month", String(period.month)); }
  const qs = params.toString();
  const { data, error: loadError, loading } = useQuery(`${qs}:${version}`, () => api<Paged<Charge>>(`/rent-charges?${qs}`));

  async function act<T>(action: () => Promise<T>, success: (r: T) => string, after?: () => void) {
    setBusy(true); setError(null); setNotice(null);
    try { const r = await action(); setNotice(success(r)); after?.(); setVersion((v) => v + 1); }
    catch (e) { setError(e instanceof Error ? e.message : "No se pudo completar la acción."); }
    finally { setBusy(false); }
  }

  if (!canReadFinance(me.activeRole)) {
    return <Leaf><EmptyState icon={<Receipt size={18} />} title="No tienes permiso para ver cargos">La información financiera no está disponible para tu rol.</EmptyState></Leaf>;
  }

  const genPeriod = parseMonthValue(genMonth);
  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Cargos</h1>
          <p className="mt-1 text-sm text-ink-soft">Renta mensual y otros cargos por contrato, con su saldo y vencimiento.</p>
        </div>
        {canWrite && (
          <div className="flex gap-2">
            <Button variant="secondary" onClick={() => setPanel(panel === "manual" ? null : "manual")}><Plus size={16} aria-hidden /> Cargo manual</Button>
            <Button onClick={() => { setPanel(panel === "generate" ? null : "generate"); setPreview(null); }}><CalendarClock size={16} aria-hidden /> Generar cargos</Button>
          </div>
        )}
      </div>

      <ErrorMessage message={error} />
      <SuccessMessage message={notice} />

      {panel === "generate" && (
        <Leaf>
          <header className="px-5 pb-4 pt-5">
            <h2 className="text-[15px] font-semibold">Generar renta del mes</h2>
            <p className="mt-0.5 text-sm text-ink-soft">Se cobra la renta completa de cada contrato vigente en el mes, sin prorrateo. Ejecutarlo de nuevo no duplica cargos.</p>
          </header>
          <div className="perforation" />
          <div className="flex flex-col gap-4 px-5 pb-5 pt-4">
            <div className="flex flex-wrap items-end gap-3">
              <div className="w-48"><Field label="Mes" type="month" value={genMonth} onChange={(e) => { setGenMonth(e.target.value); setPreview(null); }} /></div>
              <Button variant="secondary" disabled={!genPeriod} loading={busy}
                onClick={async () => {
                  if (!genPeriod) return;
                  setBusy(true); setError(null); setNotice(null);
                  try { setPreview(await api<GenerationPreview>("/rent-charges/generate-preview", { body: genPeriod })); }
                  catch (e) { setError(e instanceof Error ? e.message : "No se pudo calcular la vista previa."); }
                  finally { setBusy(false); }
                }}>Ver vista previa</Button>
            </div>

            {preview && (
              <>
                {preview.items.length === 0 ? (
                  <p className="text-sm text-ink-soft">Ningún contrato cubre {formatMonth(preview.year, preview.month)}.</p>
                ) : (
                  <ul className="overflow-hidden rounded-md border border-rule">
                    {preview.items.map((i) => (
                      <li key={i.leaseId} className="grid items-center gap-x-4 gap-y-1 border-b border-rule px-4 py-2.5 text-sm last:border-b-0 md:grid-cols-[minmax(0,1.4fr)_150px_110px_170px]">
                        <span className="min-w-0 truncate"><span className="font-mono font-medium">{i.unitCode}</span> · {i.propertyName} <span className="text-ink-soft">· {i.tenantName}</span></span>
                        <span className="whitespace-nowrap text-ink-soft md:text-right">vence {formatDate(i.dueDate)}</span>
                        <span className="font-medium md:text-right">{formatMoney(i.amount, currency)}</span>
                        <span>{i.action === "Create" ? <Badge tone="accent">Se creará</Badge> : i.action === "AlreadyExists" ? <Badge>Ya existe</Badge> : <Badge tone="pending">Omitido: renta en cero</Badge>}</span>
                      </li>
                    ))}
                  </ul>
                )}
                <div className="flex flex-wrap items-center justify-between gap-3">
                  <p className="text-sm text-ink-soft">
                    <strong className="text-ink">{preview.toCreate}</strong> por crear ({formatMoney(preview.toCreateTotal, currency)}) · {preview.alreadyExist} ya existían · {preview.skipped} omitidos
                  </p>
                  <div className="flex gap-2">
                    <Button variant="ghost" onClick={() => setPanel(null)}>Cerrar</Button>
                    <Button disabled={preview.toCreate === 0} loading={busy}
                      onClick={() => genPeriod && act(() => api<GenerationResult>("/rent-charges/generate", { body: genPeriod }),
                        (r) => `Se crearon ${r.created} cargos de ${formatMonth(r.year, r.month)} por ${formatMoney(r.createdTotal, currency)}.${r.alreadyExisted ? ` ${r.alreadyExisted} ya existían.` : ""}`,
                        () => { setPreview(null); setPanel(null); setMonth(genMonth); setPage(1); })}>
                      Generar {preview.toCreate} {preview.toCreate === 1 ? "cargo" : "cargos"}
                    </Button>
                  </div>
                </div>
              </>
            )}
          </div>
        </Leaf>
      )}

      {panel === "manual" && (
        <Leaf>
          <header className="px-5 pb-4 pt-5">
            <h2 className="text-[15px] font-semibold">Cargo manual</h2>
            <p className="mt-0.5 text-sm text-ink-soft">Para cuotas, reposiciones u otros conceptos. El importe es siempre positivo; los descuentos se manejan como ajustes, no como importes negativos.</p>
          </header>
          <div className="perforation" />
          <form noValidate className="flex flex-col gap-4 px-5 pb-5 pt-4" onSubmit={(e) => {
            e.preventDefault(); const form = e.currentTarget; const f = new FormData(form);
            if (!lease) { setError("Elige un contrato."); return; }
            // Without an explicit period the charge belongs to the month it falls due, so month filters always find it.
            const p = parseMonthValue(String(f.get("period") ?? "")) ?? parseMonthValue(String(f.get("dueDate") ?? "").slice(0, 7));
            act(() => api<Charge>("/rent-charges", { body: { leaseId: lease.id, description: f.get("description"), amount: Number(f.get("amount")), dueDate: f.get("dueDate"), periodYear: p?.year ?? null, periodMonth: p?.month ?? null } }),
              () => "Cargo creado.", () => { form.reset(); setLease(null); setPanel(null); });
          }}>
            <EntityPicker label="Contrato vigente" placeholder="Buscar por inquilino, unidad o propiedad" selected={lease} onSelect={setLease} search={searchLeases} itemKey={(l) => l.id}
              renderItem={(l) => <span><span className="font-mono font-medium">{l.unitCode}</span> · {l.propertyName} <span className="text-ink-faint">· {l.tenantName}</span></span>} />
            <div className="grid gap-4 sm:grid-cols-[minmax(0,1fr)_160px_160px]">
              <Field label="Concepto" name="description" maxLength={200} placeholder="Cuota de agua" required />
              <Field label={`Importe (${currency})`} name="amount" type="number" min={0.01} step="0.01" required />
              <Field label="Vence" name="dueDate" type="date" required />
            </div>
            <div className="max-w-xs"><Field label="Periodo" name="period" type="month" hint="Opcional. Si lo dejas vacío, se usa el mes del vencimiento." /></div>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" onClick={() => setPanel(null)}>Cancelar</Button>
              <Button type="submit" loading={busy}>Crear cargo</Button>
            </div>
          </form>
        </Leaf>
      )}

      <Leaf className="overflow-hidden">
        <div className="grid gap-3 border-b border-rule p-4 md:grid-cols-[minmax(0,1fr)_170px_200px]">
          <SearchInput label="Buscar cargos" placeholder="Buscar por inquilino, unidad, propiedad o concepto" value={search} onChange={(e) => setSearch(e.target.value)} />
          <Field label="" aria-label="Mes" type="month" value={month} onChange={(e) => { setMonth(e.target.value); setPage(1); }} className="h-10" />
          <Select aria-label="Estado del cargo" value={status} onChange={(e) => { setStatus(e.target.value as "" | ChargeStatus); setPage(1); }}>
            <option value="">Todos los estados</option>
            {(Object.keys(chargeStatusLabels) as ChargeStatus[]).map((s) => <option key={s} value={s}>{chargeStatusLabels[s]}</option>)}
          </Select>
        </div>

        <div aria-hidden className={`hidden gap-4 border-b border-rule bg-paper px-5 py-2.5 text-xs font-medium text-ink-soft md:grid ${cols}`}>
          <span>Inquilino</span><span>Concepto</span><span>Vence</span><span className="text-right">Importe</span><span className="text-right">Saldo</span><span>Estado</span>
        </div>
        {loadError && <div className="p-5"><ErrorMessage message={loadError.message} /></div>}
        {!data && !loadError && <div className="flex flex-col gap-3 p-5">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-12" />)}</div>}
        {data && data.items.length === 0 && (
          <EmptyState icon={<Receipt size={18} />} title={debounced || status || month ? "Sin resultados" : "Aún no hay cargos"}>
            {debounced || status || month ? "Prueba con otros filtros." : canWrite ? "Genera la renta del mes para los contratos vigentes, o crea un cargo manual." : undefined}
          </EmptyState>
        )}
        {data && data.items.length > 0 && (
          <ul className={loading ? "opacity-60 transition-opacity" : "transition-opacity"}>
            {data.items.map((c) => (
              <li key={c.id} className="border-b border-rule last:border-b-0">
                <div className={`grid items-center gap-x-4 gap-y-1 px-5 py-3.5 ${cols}`}>
                  <div className="min-w-0">
                    <Link href={`/tenants/${c.tenantId}`} className="block truncate text-sm font-medium hover:text-accent hover:underline">{c.tenantName}</Link>
                    <Link href={`/leases/${c.leaseId}`} className="block truncate text-sm text-ink-soft hover:text-accent hover:underline"><span className="font-mono">{c.unitCode}</span> · {c.propertyName}</Link>
                  </div>
                  <p className="min-w-0 truncate text-sm">{c.description}{c.type === "Other" && <span className="text-ink-faint"> · manual</span>}{c.type === "Deposit" && <span className="text-ink-faint"> · en custodia</span>}</p>
                  <p className="text-sm text-ink-soft"><span className="md:hidden">Vence: </span>{formatDate(c.dueDate)}</p>
                  <p className={`text-sm md:text-right ${c.status === "Voided" ? "text-ink-faint line-through" : "font-medium"}`}><span className="font-normal text-ink-soft no-underline md:hidden">Importe: </span>{formatMoney(c.amount, currency)}</p>
                  <p className={`text-sm md:text-right ${c.status === "Overdue" ? "font-semibold text-stamp" : ""}`}><span className="font-normal text-ink-soft md:hidden">Saldo: </span>{formatMoney(c.balance, currency)}
                    {c.paidAmount > 0 && c.status !== "Paid" && c.status !== "Voided" && <span className="block text-xs font-normal text-ink-soft">Pagado {formatMoney(c.paidAmount, currency)}</span>}</p>
                  <div className="flex items-center justify-between gap-2">
                    <Badge tone={chargeTone[c.status]}>{chargeStatusLabels[c.status]}</Badge>
                    {canVoid && c.status !== "Voided" && c.status !== "Paid" && (
                      <button className="text-xs text-ink-soft underline hover:text-stamp" onClick={() => setVoiding(voiding === c.id ? null : c.id)}>Anular</button>
                    )}
                  </div>
                </div>
                {c.status === "Voided" && c.voidReason && <p className="border-t border-rule bg-paper px-5 py-2 text-xs text-ink-soft">Anulado: {c.voidReason}</p>}
                {voiding === c.id && (
                  <form noValidate className="grid items-end gap-3 border-t border-rule bg-paper px-5 py-4 sm:grid-cols-[minmax(0,1fr)_auto]" onSubmit={(e) => {
                    e.preventDefault(); const reason = String(new FormData(e.currentTarget).get("reason") ?? "");
                    act(() => api(`/rent-charges/${c.id}/void`, { body: { reason } }), () => "Cargo anulado.", () => setVoiding(null));
                  }}>
                    <Field label="Motivo de la anulación" name="reason" maxLength={1000} hint="Obligatorio. Queda en la auditoría. La anulación libera el periodo para volver a generarlo." required />
                    <div className="flex gap-2"><Button type="button" variant="ghost" onClick={() => setVoiding(null)}>Cancelar</Button><Button type="submit" variant="danger" loading={busy}><Ban size={14} aria-hidden /> Anular cargo</Button></div>
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
