"use client";
import { Archive, ArrowLeft, Lock, Mail, Pencil, Phone, RotateCcw, UserRound } from "lucide-react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useState } from "react";
import { useMe } from "@/components/me-context";
import { Avatar, Badge, Button, EmptyState, ErrorMessage, Field, Leaf, Skeleton, SuccessMessage, Textarea } from "@/components/ui";
import { DownloadButton } from "@/components/download-button";
import { api, canManage, canReadLeases, canReadReports, canReadTenants, formatDate, formatMoney, formatPeriod, leaseStatusLabels, paymentStatusLabels, type Lease, type Paged, type Payment, type Tenant } from "@/lib/api";
import { leaseTone } from "@/lib/lease-ui";
import { useQuery } from "@/lib/use-query";

export default function TenantDetailPage() {
  const { id } = useParams<{ id: string }>();
  const me = useMe();
  const admin = canManage(me.activeRole);
  const allowed = canReadTenants(me.activeRole);

  const [version, setVersion] = useState(0);
  const [panel, setPanel] = useState<"edit" | "archive" | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const { data: t, error: loadError } = useQuery(`t:${id}:${version}`, () => (allowed ? api<Tenant>(`/tenants/${id}`) : Promise.reject({ message: "forbidden", status: 403 })));

  const leases = useQuery(`tl:${id}:${version}`, () => (allowed && canReadLeases(me.activeRole) ? api<Paged<Lease>>(`/leases?tenantId=${id}&pageSize=50`) : Promise.resolve(null)));
  const payments = useQuery(`tp:${id}:${version}`, () => (allowed ? api<Paged<Payment>>(`/payments?tenantId=${id}&pageSize=10`) : Promise.resolve(null)));
  const currency = me.organizations.find((o) => o.id === me.activeOrganizationId)?.currency ?? "MXN";

  async function run(body: object, success: string) {
    setBusy(true); setError(null); setNotice(null);
    try { await api(`/tenants/${id}`, { method: "PATCH", body }); setNotice(success); setPanel(null); setVersion((v) => v + 1); }
    catch (e) { setError(e instanceof Error ? e.message : "No se pudo completar la acción."); }
    finally { setBusy(false); }
  }

  if (!allowed || loadError) {
    const denied = !allowed || loadError?.status === 403;
    const missing = loadError?.status === 404;
    return (
      <Leaf>
        <EmptyState icon={denied ? <Lock size={18} /> : <UserRound size={18} />}
          title={denied ? "No tienes permiso para ver inquilinos" : missing ? "Inquilino no encontrado" : "No se pudo cargar el inquilino"}>
          {denied ? "Los datos de inquilinos no están disponibles para tu rol." : missing ? "Puede no existir o no pertenecer a tu organización." : loadError?.message}
          <Link href="/properties" className="mt-3 block font-medium text-accent hover:underline">Volver al inicio</Link>
        </EmptyState>
      </Leaf>
    );
  }
  const archived = t?.status === "Archived";

  return (
    <div className="flex flex-col gap-6">
      <div>
        <Link href="/tenants" className="mb-3 inline-flex items-center gap-1.5 text-sm text-ink-soft hover:text-ink"><ArrowLeft size={14} aria-hidden /> Inquilinos</Link>
        {!t ? <Skeleton className="h-16 w-80" /> : (
          <div className="flex flex-wrap items-start justify-between gap-4">
            <div className="flex min-w-0 items-center gap-4">
              <Avatar name={t.fullName} />
              <div className="min-w-0">
                <div className="flex items-center gap-3">
                  <h1 className="truncate text-2xl font-semibold tracking-tight">{t.fullName}</h1>
                  {archived && <Badge>Archivado</Badge>}
                </div>
                <div className="mt-1 flex flex-wrap gap-x-5 gap-y-1 text-sm text-ink-soft">
                  {t.email && <span className="inline-flex items-center gap-1.5"><Mail size={14} aria-hidden />{t.email}</span>}
                  {t.phone && <span className="inline-flex items-center gap-1.5"><Phone size={14} aria-hidden />{t.phone}</span>}
                  {!t.email && !t.phone && <span>Sin datos de contacto</span>}
                </div>
              </div>
            </div>
            <div className="flex flex-wrap gap-2">
              {canReadReports(me.activeRole) && <DownloadButton path={`/tenants/${t.id}/statement.pdf`}>Estado de cuenta (PDF)</DownloadButton>}
              {admin && (<>
                <Button variant="secondary" onClick={() => setPanel(panel === "edit" ? null : "edit")}><Pencil size={15} aria-hidden /> Editar</Button>
                {archived
                  ? <Button variant="secondary" loading={busy} onClick={() => run({ status: "Active" }, "Inquilino restaurado.")}><RotateCcw size={15} aria-hidden /> Restaurar</Button>
                  : <Button variant="danger" onClick={() => setPanel(panel === "archive" ? null : "archive")}><Archive size={15} aria-hidden /> Archivar</Button>}
              </>)}
            </div>
          </div>
        )}
      </div>

      <ErrorMessage message={error} />
      <SuccessMessage message={notice} />

      {t && panel === "edit" && (
        <Leaf>
          <header className="px-5 pb-4 pt-5"><h2 className="text-[15px] font-semibold">Editar inquilino</h2></header>
          <div className="perforation" />
          <form noValidate className="flex flex-col gap-4 px-5 pb-5 pt-4" onSubmit={(e) => {
            e.preventDefault(); const f = new FormData(e.currentTarget);
            run({ fullName: f.get("fullName"), email: f.get("email") ?? "", phone: f.get("phone") ?? "", notes: f.get("notes") ?? "" }, "Inquilino actualizado.");
          }}>
            <Field label="Nombre completo" name="fullName" defaultValue={t.fullName} maxLength={200} required />
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label="Correo electrónico" name="email" type="email" defaultValue={t.email ?? ""} />
              <Field label="Teléfono" name="phone" type="tel" defaultValue={t.phone ?? ""} />
            </div>
            <Textarea label="Notas" name="notes" defaultValue={t.notes ?? ""} maxLength={2000} />
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" onClick={() => setPanel(null)}>Cancelar</Button>
              <Button type="submit" loading={busy}>Guardar cambios</Button>
            </div>
          </form>
        </Leaf>
      )}

      {t && panel === "archive" && (
        <Leaf>
          <header className="px-5 pb-4 pt-5">
            <h2 className="text-[15px] font-semibold">Archivar inquilino</h2>
            <p className="mt-0.5 text-sm text-ink-soft">Se conserva su historial; no se elimina. Podrás restaurarlo cuando quieras.</p>
          </header>
          <div className="perforation" />
          <form className="flex flex-col gap-4 px-5 pb-5 pt-4" onSubmit={(e) => {
            e.preventDefault(); const reason = String(new FormData(e.currentTarget).get("reason") ?? "").trim();
            run({ status: "Archived", reason: reason || null }, "Inquilino archivado.");
          }}>
            <Field label="Motivo" name="reason" hint="Opcional. Queda registrado en la auditoría." maxLength={1000} />
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" onClick={() => setPanel(null)}>Cancelar</Button>
              <Button type="submit" variant="danger" loading={busy}>Archivar inquilino</Button>
            </div>
          </form>
        </Leaf>
      )}

      {t && canReadLeases(me.activeRole) && (
        <Leaf className="overflow-hidden">
          <header className="px-5 py-4"><h2 className="text-[15px] font-semibold">Contratos</h2></header>
          <div className="perforation" />
          {!leases.data && !leases.error && <div className="p-5"><Skeleton className="h-10" /></div>}
          {leases.error && <div className="p-5"><ErrorMessage message={leases.error.message} /></div>}
          {leases.data && leases.data.items.length === 0 && <p className="px-5 py-4 text-sm text-ink-soft">Este inquilino no tiene contratos.</p>}
          {leases.data && leases.data.items.length > 0 && (
            <ul>
              {leases.data.items.map((l) => (
                <li key={l.id} className="border-b border-rule last:border-b-0">
                  <Link href={`/leases/${l.id}`} className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 px-5 py-3 text-sm transition-colors hover:bg-accent-soft/40">
                    <span className="min-w-0"><span className="font-mono font-medium">{l.unitCode}</span> · {l.propertyName}<span className="block text-ink-soft">{formatPeriod(l)}</span></span>
                    <span className="flex items-center gap-3"><span className="font-medium">{formatMoney(l.monthlyRent, currency)}</span><Badge tone={leaseTone[l.status]}>{leaseStatusLabels[l.status]}</Badge></span>
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </Leaf>
      )}

      {t && (
        <Leaf className="overflow-hidden">
          <header className="flex items-center justify-between px-5 py-4">
            <h2 className="text-[15px] font-semibold">Pagos</h2>
            {admin && <Link href="/payments/new" className="text-sm font-medium text-accent hover:underline">Registrar pago</Link>}
          </header>
          <div className="perforation" />
          {!payments.data && !payments.error && <div className="p-5"><Skeleton className="h-10" /></div>}
          {payments.error && <div className="p-5"><ErrorMessage message={payments.error.message} /></div>}
          {payments.data && payments.data.items.length === 0 && <p className="px-5 py-4 text-sm text-ink-soft">Este inquilino no tiene pagos registrados.</p>}
          {payments.data && payments.data.items.length > 0 && (
            <ul>
              {payments.data.items.map((p) => (
                <li key={p.id} className="border-b border-rule last:border-b-0">
                  <Link href={`/payments/${p.id}`} className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 px-5 py-3 text-sm transition-colors hover:bg-accent-soft/40">
                    <span>{formatDate(p.receivedOn)}{p.unassignedAmount > 0 && <span className="block text-pending">Saldo a favor: {formatMoney(p.unassignedAmount, currency)}</span>}</span>
                    <span className="flex items-center gap-3"><span className={p.status === "Reversed" ? "text-ink-faint line-through" : "font-medium"}>{formatMoney(p.amount, currency)}</span><Badge tone={p.status === "Reversed" ? "stamp" : "ledger"}>{paymentStatusLabels[p.status]}</Badge></span>
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </Leaf>
      )}

      {t && (
        <Leaf>
          <header className="px-5 py-4"><h2 className="text-[15px] font-semibold">Notas</h2></header>
          <div className="perforation" />
          <p className="whitespace-pre-wrap px-5 py-4 text-sm text-ink-soft">{t.notes ?? "Sin notas."}</p>
        </Leaf>
      )}
    </div>
  );
}
