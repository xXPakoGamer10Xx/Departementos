"use client";
import { ArrowLeft, Ban, CheckCircle2, FileText, LogOut, Pencil } from "lucide-react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useState, type ReactNode } from "react";
import { useMe } from "@/components/me-context";
import { Badge, Button, EmptyState, ErrorMessage, Field, Leaf, Skeleton, SuccessMessage, Textarea } from "@/components/ui";
import { api, canManage, canReadFinance, canReadLeases, chargeStatusLabels, formatDate, formatMoney, formatPeriod, leaseStatusLabels, todayIso, type Charge, type Lease, type Paged } from "@/lib/api";
import { chargeTone } from "@/lib/charge-ui";
import { leaseTone } from "@/lib/lease-ui";
import { LeaseDocuments } from "@/components/lease-documents";
import { LeaseDeposit } from "@/components/lease-deposit";
import { LeaseInventory } from "@/components/lease-inventory";
import { useQuery } from "@/lib/use-query";

const Row = ({ label, children }: { label: string; children: ReactNode }) => (
  <div className="grid gap-1 px-5 py-3 sm:grid-cols-[180px_minmax(0,1fr)]"><dt className="text-sm text-ink-soft">{label}</dt><dd className="text-sm">{children}</dd></div>
);

export default function LeaseDetailPage() {
  const { id } = useParams<{ id: string }>();
  const me = useMe();
  const admin = canManage(me.activeRole);
  const allowed = canReadLeases(me.activeRole);
  const currency = me.organizations.find((o) => o.id === me.activeOrganizationId)?.currency ?? "MXN";

  const [version, setVersion] = useState(0);
  const [panel, setPanel] = useState<"edit" | "terminate" | "cancel" | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const { data: l, error: loadError } = useQuery(`l:${id}:${version}`, () => (allowed ? api<Lease>(`/leases/${id}`) : Promise.reject({ message: "forbidden", status: 403 })));

  const charges = useQuery(`lc:${id}:${version}`, () => (canReadFinance(me.activeRole) ? api<Paged<Charge>>(`/rent-charges?leaseId=${id}&pageSize=12`) : Promise.resolve(null)));

  async function run(path: string, method: "PATCH" | "POST", body: object | undefined, success: string) {
    setBusy(true); setError(null); setNotice(null);
    try { await api(`/leases/${id}${path}`, { method, body }); setNotice(success); setPanel(null); setVersion((v) => v + 1); }
    catch (e) { setError(e instanceof Error ? e.message : "No se pudo completar la acción."); }
    finally { setBusy(false); }
  }

  if (!allowed || loadError) {
    const denied = !allowed || loadError?.status === 403;
    const missing = loadError?.status === 404;
    return (
      <Leaf><EmptyState icon={<FileText size={18} />} title={denied ? "No tienes permiso para ver contratos" : missing ? "Contrato no encontrado" : "No se pudo cargar el contrato"}>
        {denied ? "Los contratos no están disponibles para tu rol." : missing ? "Puede no existir o no pertenecer a tu organización." : loadError?.message}
        <Link href="/leases" className="mt-3 block font-medium text-accent hover:underline">Volver a contratos</Link>
      </EmptyState></Leaf>
    );
  }

  const expired = l?.status === "Active" && l.endDate !== null && l.endDate < todayIso();
  return (
    <div className="flex flex-col gap-6">
      <div>
        <Link href="/leases" className="mb-3 inline-flex items-center gap-1.5 text-sm text-ink-soft hover:text-ink"><ArrowLeft size={14} aria-hidden /> Contratos</Link>
        {!l ? <Skeleton className="h-16 w-96" /> : (
          <div className="flex flex-wrap items-start justify-between gap-4">
            <div className="min-w-0">
              <div className="flex flex-wrap items-center gap-3">
                <h1 className="text-2xl font-semibold tracking-tight"><span className="font-mono">{l.unitCode}</span> · {l.propertyName}</h1>
                <Badge tone={leaseTone[l.status]}>{leaseStatusLabels[l.status]}</Badge>
                {expired && <Badge tone="pending">Fecha final vencida</Badge>}
              </div>
              <p className="mt-1 text-sm text-ink-soft">
                <Link href={`/tenants/${l.tenantId}`} className="font-medium text-accent hover:underline">{l.tenantName}</Link> · {formatPeriod(l)}
              </p>
            </div>
            {admin && (l.status === "Draft" || l.status === "Active") && (
              <div className="flex flex-wrap gap-2">
                <Button variant="secondary" onClick={() => setPanel(panel === "edit" ? null : "edit")}><Pencil size={15} aria-hidden /> Editar</Button>
                {l.status === "Draft" && <Button loading={busy} onClick={() => run("/activate", "POST", undefined, "Contrato activado.")}><CheckCircle2 size={15} aria-hidden /> Activar</Button>}
                {l.status === "Active" && <Button variant="secondary" onClick={() => setPanel(panel === "terminate" ? null : "terminate")}><LogOut size={15} aria-hidden /> Terminar</Button>}
                <Button variant="danger" onClick={() => setPanel(panel === "cancel" ? null : "cancel")}><Ban size={15} aria-hidden /> Cancelar</Button>
              </div>
            )}
          </div>
        )}
      </div>

      <ErrorMessage message={error} />
      <SuccessMessage message={notice} />

      {l && panel === "edit" && (
        <Leaf>
          <header className="px-5 pb-4 pt-5">
            <h2 className="text-[15px] font-semibold">Editar contrato</h2>
            <p className="mt-0.5 text-sm text-ink-soft">
              {l.status === "Active" ? "En un contrato vigente solo cambian la renta, el vencimiento, el depósito y las condiciones. Los cargos ya generados no se modifican." : "Mientras sea borrador puedes cambiar cualquier dato de vigencia y renta. Para cambiar la unidad o el inquilino, cancela y crea otro."}
            </p>
          </header>
          <div className="perforation" />
          <form noValidate className="flex flex-col gap-4 px-5 pb-5 pt-4" onSubmit={(e) => {
            e.preventDefault(); const f = new FormData(e.currentTarget);
            const body: Record<string, unknown> = { monthlyRent: Number(f.get("rent")), dueDay: Number(f.get("dueDay")), termsNotes: f.get("terms") ?? "" };
            if (f.get("deposit") !== "") body.depositAmount = Number(f.get("deposit"));
            body.guarantorName = f.get("guarantorName") ?? ""; if (String(f.get("guarantorName") ?? "").trim()) body.guarantorPhone = f.get("guarantorPhone") ?? "";
            if (l.status === "Draft") { body.startDate = f.get("startDate"); if (f.get("endDate")) body.endDate = f.get("endDate"); else body.clearEndDate = true; }
            run("", "PATCH", body, "Contrato actualizado.");
          }}>
            {l.status === "Draft" && (
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label="Inicio" name="startDate" type="date" defaultValue={l.startDate} required />
                <Field label="Fin" name="endDate" type="date" defaultValue={l.endDate ?? ""} hint="Vacío = sin fecha final." />
              </div>
            )}
            <div className="grid gap-4 sm:grid-cols-3">
              <Field label={`Renta mensual (${currency})`} name="rent" type="number" min={0} step="0.01" defaultValue={l.monthlyRent} required />
              <Field label="Día de vencimiento" name="dueDay" type="number" min={1} max={28} defaultValue={l.dueDay} required />
              <Field label={`Depósito (${currency})`} name="deposit" type="number" min={0} step="0.01" defaultValue={l.depositAmount ?? ""} />
            </div>
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label="Fiador (opcional)" name="guarantorName" defaultValue={l.guarantorName ?? ""} maxLength={200} hint="Déjalo vacío si no hay fiador." />
              <Field label="Teléfono del fiador" name="guarantorPhone" defaultValue={l.guarantorPhone ?? ""} maxLength={30} />
            </div>
            <Textarea label="Condiciones y notas" name="terms" defaultValue={l.termsNotes ?? ""} maxLength={4000} />
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" onClick={() => setPanel(null)}>Cancelar</Button>
              <Button type="submit" loading={busy}>Guardar cambios</Button>
            </div>
          </form>
        </Leaf>
      )}

      {l && panel === "terminate" && (
        <Leaf>
          <header className="px-5 pb-4 pt-5">
            <h2 className="text-[15px] font-semibold">Terminar contrato</h2>
            <p className="mt-0.5 text-sm text-ink-soft">La unidad queda libre después de la fecha de terminación. No se borran cargos ni pagos, y el último mes no se prorratea.</p>
          </header>
          <div className="perforation" />
          <form noValidate className="flex flex-col gap-4 px-5 pb-5 pt-4" onSubmit={(e) => {
            e.preventDefault(); const f = new FormData(e.currentTarget);
            run("/terminate", "POST", { endDate: f.get("endDate"), reason: f.get("reason") }, "Contrato terminado.");
          }}>
            <div className="grid gap-4 sm:grid-cols-[200px_minmax(0,1fr)]">
              <Field label="Fecha de terminación" name="endDate" type="date" defaultValue={todayIso() < l.startDate ? l.startDate : todayIso()} min={l.startDate} max={l.endDate ?? undefined} required />
              <Field label="Motivo" name="reason" maxLength={1000} hint="Obligatorio. Queda en la auditoría." required />
            </div>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" onClick={() => setPanel(null)}>Cancelar</Button>
              <Button type="submit" variant="danger" loading={busy}>Terminar contrato</Button>
            </div>
          </form>
        </Leaf>
      )}

      {l && panel === "cancel" && (
        <Leaf>
          <header className="px-5 pb-4 pt-5">
            <h2 className="text-[15px] font-semibold">Cancelar contrato</h2>
            <p className="mt-0.5 text-sm text-ink-soft">Úsalo cuando el contrato no debió existir o nunca entró en vigor. Libera la unidad de inmediato; el registro se conserva.</p>
          </header>
          <div className="perforation" />
          <form noValidate className="flex flex-col gap-4 px-5 pb-5 pt-4" onSubmit={(e) => {
            e.preventDefault(); run("/cancel", "POST", { reason: new FormData(e.currentTarget).get("reason") }, "Contrato cancelado.");
          }}>
            <Field label="Motivo" name="reason" maxLength={1000} hint="Obligatorio. Queda en la auditoría." required />
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" onClick={() => setPanel(null)}>Volver</Button>
              <Button type="submit" variant="danger" loading={busy}>Cancelar contrato</Button>
            </div>
          </form>
        </Leaf>
      )}

      {l && (
        <Leaf className="overflow-hidden">
          <header className="px-5 py-4"><h2 className="text-[15px] font-semibold">Condiciones</h2></header>
          <div className="perforation" />
          <dl className="divide-y divide-rule">
            <Row label="Renta mensual"><span className="font-medium">{formatMoney(l.monthlyRent, currency)}</span></Row>
            <Row label="Vence el día">{l.dueDay} de cada mes</Row>
            <Row label="Vigencia">{formatPeriod(l)}</Row>
            <Row label="Depósito">{l.depositAmount != null ? formatMoney(l.depositAmount, currency) : "Sin depósito"} <span className="text-ink-faint">· se guarda en custodia, no cuenta como ingreso</span></Row>
            <Row label="Fiador">{l.guarantorName ? <>{l.guarantorName}{l.guarantorPhone ? <span className="text-ink-soft"> · {l.guarantorPhone}</span> : null}</> : <span className="text-ink-soft">Sin fiador</span>}</Row>
            {l.closureReason && <Row label={l.status === "Cancelled" ? "Motivo de cancelación" : "Motivo de terminación"}>{l.closureReason}{l.status === "Ended" && l.endDate ? <span className="text-ink-faint"> · terminó el {formatDate(l.endDate)}</span> : null}</Row>}
            <Row label="Condiciones y notas"><span className="whitespace-pre-wrap text-ink-soft">{l.termsNotes ?? "Sin notas."}</span></Row>
          </dl>
        </Leaf>
      )}

      {l && l.status !== "Draft" && l.status !== "Cancelled" && (
        <Leaf className="overflow-hidden">
          <header className="flex items-center justify-between px-5 py-4">
            <h2 className="text-[15px] font-semibold">Cargos</h2>
            <Link href="/charges" className="text-sm font-medium text-accent hover:underline">Ver todos</Link>
          </header>
          <div className="perforation" />
          {!charges.data && !charges.error && <div className="p-5"><Skeleton className="h-10" /></div>}
          {charges.error && <div className="p-5"><ErrorMessage message={charges.error.message} /></div>}
          {charges.data && charges.data.items.length === 0 && <p className="px-5 py-4 text-sm text-ink-soft">Aún no hay cargos para este contrato. Se generan desde la sección Cargos.</p>}
          {charges.data && charges.data.items.length > 0 && (
            <ul>
              {charges.data.items.map((c) => (
                <li key={c.id} className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 border-b border-rule px-5 py-3 text-sm last:border-b-0">
                  <span className="min-w-0">{c.description}<span className="block text-ink-soft">vence {formatDate(c.dueDate)}</span></span>
                  <span className="flex items-center gap-3"><span className="font-medium">{formatMoney(c.amount, currency)}</span><Badge tone={chargeTone[c.status]}>{chargeStatusLabels[c.status]}</Badge></span>
                </li>
              ))}
            </ul>
          )}
        </Leaf>
      )}
      {l && l.status !== "Cancelled" && <LeaseInventory leaseId={l.id} status={l.status} canEdit={admin} />}
      {l && l.depositAmount != null && l.status !== "Draft" && canReadFinance(me.activeRole) && <LeaseDeposit leaseId={l.id} currency={currency} canEdit={admin} onChanged={() => setVersion((v) => v + 1)} />}
      {l && <LeaseDocuments leaseId={l.id} canGenerate={admin} cancelled={l.status === "Cancelled"} />}
    </div>
  );
}
