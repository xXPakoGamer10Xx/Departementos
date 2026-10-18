"use client";
import { FileCheck2, Lock, TriangleAlert } from "lucide-react";
import Link from "next/link";
import { useState } from "react";
import { ChargeAllocator } from "@/components/charge-allocator";
import { ClaimProof } from "@/components/claim-proof";
import { useMe } from "@/components/me-context";
import { Badge, Button, EmptyState, ErrorMessage, Leaf, Pager, Select, Skeleton, SuccessMessage, Textarea, type Tone } from "@/components/ui";
import { api, canWritePayments, claimStatusLabels, formatDate, formatDateTime, formatMoney, fromCents, paymentMethodLabels, toCents, type ClaimStatus, type Paged, type PaymentClaim } from "@/lib/api";
import { useQuery } from "@/lib/use-query";

const tone: Record<ClaimStatus, Tone> = { Pending: "pending", Confirmed: "ledger", Rejected: "stamp", Cancelled: "neutral" };
const PAGE_SIZE = 10;

export default function ClaimsPage() {
  const me = useMe();
  const currency = me.organizations.find((o) => o.id === me.activeOrganizationId)?.currency ?? "MXN";
  const [status, setStatus] = useState<ClaimStatus | "">("Pending");
  const [page, setPage] = useState(1);
  const [version, setVersion] = useState(0);
  const [notice, setNotice] = useState<string | null>(null);
  const qs = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE), ...(status ? { status } : {}) }).toString();
  const { data, error, loading } = useQuery(`claims:${qs}:${version}`, () => api<Paged<PaymentClaim>>(`/payment-claims?${qs}`));

  if (!canWritePayments(me.activeRole)) {
    return <Leaf><EmptyState icon={<Lock size={18} />} title="No tienes permiso para revisar reportes de pago">Solo propietarios, administradores, finanzas y cobradores pueden hacerlo.</EmptyState></Leaf>;
  }

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Reportes de pago</h1>
          <p className="mt-1 text-sm text-ink-soft">Lo que los inquilinos dicen haber pagado. Nada cuenta como ingreso hasta que lo confirmas.</p>
        </div>
        <div className="w-44"><Select label="Estado" value={status} onChange={(e) => { setStatus(e.target.value as ClaimStatus | ""); setPage(1); }}>
          <option value="Pending">Por revisar</option><option value="Confirmed">Confirmados</option><option value="Rejected">Rechazados</option><option value="Cancelled">Cancelados</option><option value="">Todos</option>
        </Select></div>
      </div>
      <SuccessMessage message={notice} />
      <ErrorMessage message={error?.message ?? null} />
      {!data && !error && <div className="flex flex-col gap-3">{[0, 1].map((i) => <Leaf key={i}><Skeleton className="m-5 h-24" /></Leaf>)}</div>}
      {data?.items.length === 0 && (
        <Leaf><EmptyState icon={<FileCheck2 size={18} />} title={status === "Pending" ? "No hay reportes por revisar" : "No hay reportes con ese estado"}>
          {status === "Pending" ? "Cuando un inquilino reporte un pago con su comprobante, aparecerá aquí." : "Prueba con otro filtro."}</EmptyState></Leaf>
      )}
      <div className={loading ? "flex flex-col gap-4 opacity-60" : "flex flex-col gap-4"}>
        {data?.items.map((c) => <ClaimCard key={c.id} claim={c} currency={currency} onChanged={(msg) => { setNotice(msg); setVersion((v) => v + 1); }} />)}
      </div>
      {data && data.total > PAGE_SIZE && <Leaf><Pager page={page} pageSize={PAGE_SIZE} total={data.total} onPage={setPage} /></Leaf>}
    </div>
  );
}

function ClaimCard({ claim: c, currency, onChanged }: { claim: PaymentClaim; currency: string; onChanged: (message: string) => void }) {
  const [panel, setPanel] = useState<"confirm" | "reject" | null>(null);
  const [applied, setApplied] = useState<Record<string, string>>({});
  const [comment, setComment] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const appliedCents = Object.values(applied).reduce((sum, v) => sum + toCents(v), 0);
  const tooMuch = appliedCents > toCents(c.amount);

  async function run(action: () => Promise<unknown>, success: string) {
    setBusy(true); setError(null);
    try { await action(); onChanged(success); }
    catch (e) { setError(e instanceof Error ? e.message : "No se pudo completar la acción."); setBusy(false); }
  }

  return (
    <Leaf>
      <div className="flex flex-wrap items-start justify-between gap-4 px-5 pb-3 pt-5">
        <div className="min-w-0">
          <Link href={`/tenants/${c.tenantId}`} className="text-[15px] font-semibold hover:text-accent hover:underline">{c.tenantName}</Link>
          <p className="mt-0.5 text-xs text-ink-faint">Pagó el {formatDate(c.paidOn)} · {paymentMethodLabels[c.method]}{c.reference ? ` · ${c.reference}` : ""} · enviado {formatDateTime(c.createdAt)}</p>
        </div>
        <div className="text-right">
          <p className="font-mono text-xl font-semibold tabular-nums">{formatMoney(c.amount, currency)}</p>
          <div className="mt-1 flex justify-end gap-1.5">{c.duplicateProof && c.status === "Pending" && <Badge tone="stamp">Comprobante repetido</Badge>}<Badge tone={tone[c.status]}>{claimStatusLabels[c.status]}</Badge></div>
        </div>
      </div>
      {c.duplicateProof && (
        <p className="mx-5 mb-3 flex items-start gap-2 rounded-md bg-stamp-soft px-3 py-2 text-sm text-stamp"><TriangleAlert size={15} className="mt-0.5 shrink-0" aria-hidden />
          Este mismo archivo está en otro reporte. Revisa que no se esté cobrando dos veces.</p>
      )}
      {c.tenantNote && <p className="px-5 pb-3 text-sm text-ink-soft">«{c.tenantNote}»</p>}
      <div className="px-5 pb-4"><ClaimProof file={c.file} /></div>

      {c.status !== "Pending" && (
        <div className="border-t border-rule bg-paper px-5 py-3 text-sm text-ink-soft">
          {c.status === "Confirmed" && <>Confirmado{c.reviewedByName ? ` por ${c.reviewedByName}` : ""}{c.reviewedAt ? ` el ${formatDateTime(c.reviewedAt)}` : ""}. {c.paymentId && <Link href={`/payments/${c.paymentId}`} className="font-medium text-accent hover:underline">Ver el pago</Link>}</>}
          {c.status === "Rejected" && <>Rechazado{c.reviewedByName ? ` por ${c.reviewedByName}` : ""}: <span className="text-ink">{c.reviewComment}</span></>}
          {c.status === "Cancelled" && <>El inquilino canceló este reporte.</>}
        </div>
      )}

      {c.status === "Pending" && (
        <>
          <div className="flex flex-wrap gap-2 border-t border-rule px-5 py-3">
            <Button size="sm" onClick={() => setPanel(panel === "confirm" ? null : "confirm")}>Confirmar pago</Button>
            <Button size="sm" variant="danger" onClick={() => setPanel(panel === "reject" ? null : "reject")}>Rechazar</Button>
          </div>
          {panel === "confirm" && (
            <div className="flex flex-col gap-4 border-t border-rule bg-paper px-5 py-4">
              <p className="text-sm text-ink-soft">Se registrará un pago de <strong className="text-ink">{formatMoney(c.amount, currency)}</strong> con fecha {formatDate(c.paidOn)}. Elige a qué cargos aplicarlo; lo que no apliques queda como saldo a favor.</p>
              <ChargeAllocator tenantId={c.tenantId} currency={currency} values={applied} onChange={setApplied} available={c.amount} />
              <p className={`text-sm ${tooMuch ? "text-stamp" : "text-ink-soft"}`}>Aplicado {formatMoney(fromCents(appliedCents), currency)} · saldo a favor {formatMoney(fromCents(Math.max(toCents(c.amount) - appliedCents, 0)), currency)}</p>
              <ErrorMessage message={error} />
              <div className="flex gap-2">
                <Button loading={busy} disabled={tooMuch} onClick={() => run(() => api(`/payment-claims/${c.id}/confirm`, { body: {
                  allocations: Object.entries(applied).filter(([, v]) => toCents(v) > 0).map(([chargeId, v]) => ({ chargeId, amount: fromCents(toCents(v)) })),
                } }), "Pago confirmado y registrado.")}>Confirmar y registrar</Button>
                <Button variant="ghost" onClick={() => setPanel(null)} disabled={busy}>Cerrar</Button>
              </div>
            </div>
          )}
          {panel === "reject" && (
            <form noValidate className="flex flex-col gap-3 border-t border-rule bg-paper px-5 py-4" onSubmit={(e) => { e.preventDefault(); if (!comment.trim()) { setError("Explica al inquilino por qué se rechaza."); return; } run(() => api(`/payment-claims/${c.id}/reject`, { body: { comment } }), "Reporte rechazado."); }}>
              <Textarea label="Motivo para el inquilino" value={comment} onChange={(e) => setComment(e.target.value)} maxLength={500} hint="Lo verá el inquilino. Obligatorio." required />
              <ErrorMessage message={error} />
              <div className="flex gap-2"><Button type="submit" variant="danger" loading={busy}>Rechazar reporte</Button><Button type="button" variant="ghost" onClick={() => setPanel(null)} disabled={busy}>Cerrar</Button></div>
            </form>
          )}
        </>
      )}
    </Leaf>
  );
}
