"use client";
import { ArrowLeft, HandCoins, Lock, Plus, Undo2 } from "lucide-react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useState, type ReactNode } from "react";
import { ChargeAllocator } from "@/components/charge-allocator";
import { useMe } from "@/components/me-context";
import { Badge, Button, EmptyState, ErrorMessage, Field, Leaf, Skeleton, SuccessMessage } from "@/components/ui";
import { api, canManage, canReadFinance, canWritePayments, formatDate, formatMoney, fromCents, paymentMethodLabels, paymentStatusLabels, toCents, type Payment } from "@/lib/api";
import { DownloadButton } from "@/components/download-button";
import { useQuery } from "@/lib/use-query";

const Row = ({ label, children }: { label: string; children: ReactNode }) => (
  <div className="grid gap-1 px-5 py-3 sm:grid-cols-[180px_minmax(0,1fr)]"><dt className="text-sm text-ink-soft">{label}</dt><dd className="text-sm">{children}</dd></div>
);

export default function PaymentDetailPage() {
  const { id } = useParams<{ id: string }>();
  const me = useMe();
  const allowed = canReadFinance(me.activeRole);
  const currency = me.organizations.find((o) => o.id === me.activeOrganizationId)?.currency ?? "MXN";

  const [version, setVersion] = useState(0);
  const [panel, setPanel] = useState<"allocate" | "reverse" | null>(null);
  const [undoing, setUndoing] = useState<string | null>(null);
  const [applied, setApplied] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const { data: p, error: loadError } = useQuery(`pay:${id}:${version}`, () => (allowed ? api<Payment>(`/payments/${id}`) : Promise.reject({ message: "forbidden", status: 403 })));

  async function run(action: () => Promise<unknown>, success: string, after?: () => void) {
    setBusy(true); setError(null); setNotice(null);
    try { await action(); setNotice(success); after?.(); setVersion((v) => v + 1); }
    catch (e) { setError(e instanceof Error ? e.message : "No se pudo completar la acción."); }
    finally { setBusy(false); }
  }

  if (!allowed || loadError) {
    const denied = !allowed || loadError?.status === 403;
    return (
      <Leaf><EmptyState icon={denied ? <Lock size={18} /> : <HandCoins size={18} />}
        title={denied ? "No tienes permiso para ver pagos" : loadError?.status === 404 ? "Pago no encontrado" : "No se pudo cargar el pago"}>
        {denied ? "La información financiera no está disponible para tu rol." : loadError?.status === 404 ? "Puede no existir o no pertenecer a tu organización." : loadError?.message}
        <Link href="/payments" className="mt-3 block font-medium text-accent hover:underline">Volver a pagos</Link>
      </EmptyState></Leaf>
    );
  }

  const reversed = p?.status === "Reversed";
  const appliedCents = Object.values(applied).reduce((sum, v) => sum + toCents(v), 0);
  const overUnassigned = p ? appliedCents > toCents(p.unassignedAmount) : false;

  return (
    <div className="flex flex-col gap-6">
      <div>
        <Link href="/payments" className="mb-3 inline-flex items-center gap-1.5 text-sm text-ink-soft hover:text-ink"><ArrowLeft size={14} aria-hidden /> Pagos</Link>
        {!p ? <Skeleton className="h-16 w-96" /> : (
          <div className="flex flex-wrap items-start justify-between gap-4">
            <div className="min-w-0">
              <div className="flex flex-wrap items-center gap-3">
                <h1 className={`text-2xl font-semibold tracking-tight ${reversed ? "text-ink-faint line-through" : ""}`}>{formatMoney(p.amount, currency)}</h1>
                <Badge tone={reversed ? "stamp" : "ledger"}>{paymentStatusLabels[p.status]}</Badge>
                {p.receiptFolio && <Badge tone="neutral"><span className="font-mono">{p.receiptFolio}</span></Badge>}
              </div>
              <p className="mt-1 text-sm text-ink-soft">
                <Link href={`/tenants/${p.tenantId}`} className="font-medium text-accent hover:underline">{p.tenantName}</Link> · {formatDate(p.receivedOn)}
              </p>
            </div>
            <div className="flex flex-wrap gap-2">
              <DownloadButton path={`/payments/${p.id}/receipt.pdf`}>{reversed ? "Recibo cancelado (PDF)" : "Recibo (PDF)"}</DownloadButton>
              {!reversed && (<>
                {canWritePayments(me.activeRole) && p.unassignedAmount > 0 && <Button onClick={() => { setPanel(panel === "allocate" ? null : "allocate"); setApplied({}); }}><Plus size={15} aria-hidden /> Aplicar saldo</Button>}
                {canManage(me.activeRole) && <Button variant="danger" onClick={() => setPanel(panel === "reverse" ? null : "reverse")}><Undo2 size={15} aria-hidden /> Revertir pago</Button>}
              </>)}
            </div>
          </div>
        )}
      </div>

      <ErrorMessage message={error} />
      <SuccessMessage message={notice} />

      {p && !reversed && (
        <Leaf>
          <dl className="grid divide-y divide-rule sm:grid-cols-3 sm:divide-x sm:divide-y-0">
            <div className="px-5 py-4"><dt className="text-sm text-ink-soft">Importe</dt><dd className="mt-0.5 text-lg font-semibold">{formatMoney(p.amount, currency)}</dd></div>
            <div className="px-5 py-4"><dt className="text-sm text-ink-soft">Aplicado a cargos</dt><dd className="mt-0.5 text-lg font-semibold">{formatMoney(p.allocatedAmount, currency)}</dd></div>
            <div className="px-5 py-4"><dt className="text-sm text-ink-soft">Saldo a favor</dt><dd className={`mt-0.5 text-lg font-semibold ${p.unassignedAmount > 0 ? "text-pending" : ""}`}>{formatMoney(p.unassignedAmount, currency)}</dd></div>
          </dl>
        </Leaf>
      )}

      {p && panel === "allocate" && (
        <Leaf>
          <header className="px-5 pb-4 pt-5">
            <h2 className="text-[15px] font-semibold">Aplicar saldo a favor</h2>
            <p className="mt-0.5 text-sm text-ink-soft">Disponible: {formatMoney(p.unassignedAmount, currency)}. Se aplica solo a los cargos que elijas; nunca se asigna automáticamente a meses futuros.</p>
          </header>
          <div className="perforation" />
          <div className="flex flex-col gap-4 px-5 pb-5 pt-4">
            <ChargeAllocator tenantId={p.tenantId} currency={currency} values={applied} onChange={setApplied} available={p.unassignedAmount} />
            {overUnassigned && <p role="alert" className="text-sm text-stamp">Lo aplicado supera el saldo a favor disponible.</p>}
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" onClick={() => setPanel(null)}>Cancelar</Button>
              <Button disabled={appliedCents <= 0 || overUnassigned} loading={busy}
                onClick={() => run(() => api(`/payments/${id}/allocations`, { body: { allocations: Object.entries(applied).filter(([, v]) => toCents(v) > 0).map(([chargeId, v]) => ({ chargeId, amount: fromCents(toCents(v)) })) } }),
                  `Se aplicaron ${formatMoney(fromCents(appliedCents), currency)}.`, () => { setPanel(null); setApplied({}); })}>
                Aplicar {formatMoney(fromCents(appliedCents), currency)}
              </Button>
            </div>
          </div>
        </Leaf>
      )}

      {p && panel === "reverse" && (
        <Leaf>
          <header className="px-5 pb-4 pt-5">
            <h2 className="text-[15px] font-semibold">Revertir pago</h2>
            <p className="mt-0.5 text-sm text-ink-soft">Se revierten todas sus aplicaciones y los cargos recuperan su saldo. El pago se conserva en el historial; no se puede deshacer.</p>
          </header>
          <div className="perforation" />
          <form noValidate className="flex flex-col gap-4 px-5 pb-5 pt-4" onSubmit={(e) => {
            e.preventDefault(); const reason = String(new FormData(e.currentTarget).get("reason") ?? "");
            run(() => api(`/payments/${id}/reverse`, { body: { reason } }), "Pago revertido.", () => setPanel(null));
          }}>
            <Field label="Motivo" name="reason" maxLength={900} hint="Obligatorio. Queda en la auditoría." required />
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" onClick={() => setPanel(null)}>Cancelar</Button>
              <Button type="submit" variant="danger" loading={busy}>Revertir pago</Button>
            </div>
          </form>
        </Leaf>
      )}

      {p && (
        <Leaf className="overflow-hidden">
          <header className="px-5 py-4"><h2 className="text-[15px] font-semibold">Aplicaciones</h2></header>
          <div className="perforation" />
          {(p.allocations ?? []).length === 0 && <p className="px-5 py-4 text-sm text-ink-soft">Este pago no se ha aplicado a ningún cargo.</p>}
          <ul>
            {(p.allocations ?? []).map((a) => (
              <li key={a.id} className="border-b border-rule last:border-b-0">
                <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 px-5 py-3 text-sm">
                  <span className="min-w-0"><span className={a.reversedAt ? "text-ink-faint line-through" : "font-medium"}>{a.chargeDescription}</span>
                    <span className="block text-ink-soft"><span className="font-mono">{a.unitCode}</span> · {a.propertyName}</span></span>
                  <span className="flex items-center gap-3">
                    <span className={a.reversedAt ? "text-ink-faint line-through" : "font-medium"}>{formatMoney(a.amount, currency)}</span>
                    {a.reversedAt ? <Badge tone="stamp">Revertida</Badge>
                      : canManage(me.activeRole) && !reversed && <button className="text-xs text-ink-soft underline hover:text-stamp" onClick={() => setUndoing(undoing === a.id ? null : a.id)}>Revertir</button>}
                  </span>
                </div>
                {a.reversedAt && a.reversalReason && <p className="border-t border-rule bg-paper px-5 py-2 text-xs text-ink-soft">Revertida: {a.reversalReason}</p>}
                {undoing === a.id && (
                  <form noValidate className="grid items-end gap-3 border-t border-rule bg-paper px-5 py-4 sm:grid-cols-[minmax(0,1fr)_auto]" onSubmit={(e) => {
                    e.preventDefault(); const reason = String(new FormData(e.currentTarget).get("reason") ?? "");
                    run(() => api(`/payments/${id}/allocations/${a.id}/reverse`, { body: { reason } }), "Aplicación revertida. El importe vuelve a ser saldo a favor.", () => setUndoing(null));
                  }}>
                    <Field label="Motivo" name="reason" maxLength={900} hint="Obligatorio. El importe quedará libre para aplicarlo a otro cargo." required />
                    <div className="flex gap-2"><Button type="button" variant="ghost" onClick={() => setUndoing(null)}>Cancelar</Button><Button type="submit" variant="danger" loading={busy}>Revertir aplicación</Button></div>
                  </form>
                )}
              </li>
            ))}
          </ul>
        </Leaf>
      )}

      {p && (
        <Leaf className="overflow-hidden">
          <header className="px-5 py-4"><h2 className="text-[15px] font-semibold">Detalles</h2></header>
          <div className="perforation" />
          <dl className="divide-y divide-rule">
            <Row label="Método">{paymentMethodLabels[p.method]}</Row>
            <Row label="Referencia">{p.reference ?? <span className="text-ink-faint">Sin referencia</span>}</Row>
            <Row label="Fecha de recepción">{formatDate(p.receivedOn)}</Row>
            <Row label="Notas"><span className="whitespace-pre-wrap text-ink-soft">{p.notes ?? "Sin notas."}</span></Row>
            {reversed && <Row label="Motivo del reverso">{p.reversalReason}</Row>}
          </dl>
        </Leaf>
      )}
    </div>
  );
}
