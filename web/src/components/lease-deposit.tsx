"use client";
import { Plus, Trash2, Vault } from "lucide-react";
import { useState } from "react";
import { Badge, Button, ErrorMessage, Field, Leaf, Skeleton, SuccessMessage, Textarea } from "@/components/ui";
import { api, chargeStatusLabels, formatDate, formatMoney, todayIso, type Deposit } from "@/lib/api";
import { chargeTone } from "@/lib/charge-ui";
import { useQuery } from "@/lib/use-query";

type Row = { dueDate: string; amount: string };
type Deduction = { concept: string; amount: string };

/** Splits an amount into n installments of whole cents; the last one absorbs the rounding so they always add up exactly. */
export function splitAmount(total: number, n: number): string[] {
  const cents = Math.round(total * 100), base = Math.floor(cents / n);
  return Array.from({ length: n }, (_, i) => ((i === n - 1 ? cents - base * (n - 1) : base) / 100).toFixed(2));
}
const addMonths = (iso: string, m: number) => { const [y, mo, d] = iso.split("-").map(Number); const t = new Date(y, mo - 1 + m, Math.min(d, 28)); return `${t.getFullYear()}-${String(t.getMonth() + 1).padStart(2, "0")}-${String(t.getDate()).padStart(2, "0")}`; };

/**
 * The deposit of a lease: money held for the tenant, not income. It is collected in installments (each one a charge of its own), and when the lease is over it is
 * settled: what is kept (each with its reason) and what is given back.
 */
export function LeaseDeposit({ leaseId, currency, canEdit, onChanged }: { leaseId: string; currency: string; canEdit: boolean; onChanged: () => void }) {
  const [version, setVersion] = useState(0);
  const dep = useQuery(`dep:${leaseId}:${version}`, () => api<Deposit>(`/leases/${leaseId}/deposit`));
  const [mode, setMode] = useState<"view" | "schedule" | "settle" | "void">("view");
  const [rows, setRows] = useState<Row[]>([]);
  const [deductions, setDeductions] = useState<Deduction[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const d = dep.data;
  const money = (n: number) => formatMoney(n, currency);

  async function post(path: string, body: unknown, message: string) {
    setBusy(true); setError(null); setNotice(null);
    try { await api(`/leases/${leaseId}/deposit/${path}`, { method: "POST", body }); setNotice(message); setMode("view"); setVersion((v) => v + 1); onChanged(); }
    catch (e) { setError(e instanceof Error ? e.message : "No se pudo completar la acción."); }
    finally { setBusy(false); }
  }
  const divide = (n: number) => { if (!d) return; const amounts = splitAmount(d.unscheduled, n); setRows(amounts.map((a, i) => ({ dueDate: addMonths(todayIso(), i), amount: a }))); };
  const rowsTotal = rows.reduce((s, r) => s + (Number(r.amount) || 0), 0);
  const deductionsTotal = deductions.reduce((s, r) => s + (Number(r.amount) || 0), 0);
  const ended = d && (d.leaseStatus === "Ended" || d.leaseStatus === "Cancelled");

  return (
    <Leaf className="overflow-hidden">
      <header className="flex flex-wrap items-center justify-between gap-2 px-5 py-4">
        <div><h2 className="text-[15px] font-semibold">Depósito en custodia</h2><p className="mt-0.5 text-sm text-ink-soft">Dinero del inquilino que se guarda y se devuelve; no cuenta como ingreso.</p></div>
        {canEdit && d && mode === "view" && (
          <div className="flex gap-2">
            {!ended && d.unscheduled > 0 && <Button variant="secondary" onClick={() => { setRows([{ dueDate: todayIso(), amount: d.unscheduled.toFixed(2) }]); setMode("schedule"); }}><Plus size={15} aria-hidden /> Programar cobro</Button>}
            {ended && !d.settlement && d.paid > 0 && <Button onClick={() => { setDeductions([]); setMode("settle"); }}><Vault size={15} aria-hidden /> Liquidar depósito</Button>}
            {d.settlement && <Button variant="secondary" onClick={() => setMode("void")}>Anular liquidación</Button>}
          </div>
        )}
      </header>
      <div className="perforation" />
      <div className="flex flex-col gap-4 px-5 py-4">
        <ErrorMessage message={error ?? dep.error?.message ?? null} /><SuccessMessage message={notice} />
        {!d && !dep.error && <Skeleton className="h-16" />}
        {d && (
          <dl className="grid gap-3 text-sm sm:grid-cols-4">
            <div><dt className="text-ink-soft">Según el contrato</dt><dd className="font-medium">{money(d.depositAmount ?? 0)}</dd></div>
            <div><dt className="text-ink-soft">Cobrado</dt><dd className="font-medium">{money(d.paid)}</dd></div>
            <div><dt className="text-ink-soft">Por cobrar</dt><dd className="font-medium">{money(d.outstanding)}</dd></div>
            <div><dt className="text-ink-soft">En custodia ahora</dt><dd className="font-medium">{money(d.custody)}</dd></div>
          </dl>
        )}
        {d && d.unscheduled > 0 && mode === "view" && !ended && <p className="text-sm text-ink-soft">Faltan {money(d.unscheduled)} por programar en abonos.</p>}

        {d && d.installments.length > 0 && (
          <ul className="divide-y divide-rule rounded-md border border-rule">{d.installments.map((i) => (
            <li key={i.chargeId} className="flex flex-wrap items-center justify-between gap-2 px-4 py-2.5 text-sm">
              <span>{i.description}<span className="block text-ink-soft">vence {formatDate(i.dueDate)}</span></span>
              <span className="flex items-center gap-3"><span className="font-medium">{money(i.amount)}</span><Badge tone={chargeTone[i.status]}>{chargeStatusLabels[i.status]}</Badge></span>
            </li>))}</ul>
        )}

        {d?.settlement && (
          <div className="rounded-md border border-rule bg-paper px-4 py-3 text-sm">
            <p className="font-medium">Liquidado el {formatDate(d.settlement.settledOn)}</p>
            <p className="mt-1">Se devuelven <strong>{money(d.settlement.refund)}</strong> de {money(d.settlement.held)}{d.settlement.deductionsTotal > 0 && <> · se retienen {money(d.settlement.deductionsTotal)}</>}</p>
            {d.settlement.deductions.length > 0 && <ul className="mt-2 list-disc pl-5 text-ink-soft">{d.settlement.deductions.map((x, n) => <li key={n}>{x.concept}: {money(x.amount)}</li>)}</ul>}
            {d.settlement.notes && <p className="mt-2 text-ink-soft">{d.settlement.notes}</p>}
          </div>
        )}
        {d && d.voidedSettlements.length > 0 && (
          <details className="text-sm text-ink-soft"><summary className="cursor-pointer">Liquidaciones anuladas ({d.voidedSettlements.length})</summary>
            <ul className="mt-2 list-disc pl-5">{d.voidedSettlements.map((v) => <li key={v.id}>{formatDate(v.settledOn)} · {money(v.refund)} devueltos · anulada: {v.voidReason}</li>)}</ul></details>
        )}

        {mode === "schedule" && d && (
          <form noValidate className="flex flex-col gap-3" onSubmit={(e) => {
            e.preventDefault();
            if (Math.abs(rowsTotal - d.unscheduled) > 0.001) { setError(`Los abonos suman ${money(rowsTotal)} y deben sumar ${money(d.unscheduled)}.`); return; }
            post("schedule", { installments: rows.map((r) => ({ dueDate: r.dueDate, amount: Number(r.amount) })) }, "Cobro del depósito programado.");
          }}>
            <div className="flex flex-wrap items-center gap-2 text-sm">Dividir en
              {[1, 2, 3, 4, 6].map((n) => <Button key={n} type="button" variant="secondary" onClick={() => divide(n)}>{n === 1 ? "1 pago" : `${n} abonos`}</Button>)}</div>
            {rows.map((r, i) => (
              <div key={i} className="grid items-end gap-2 sm:grid-cols-[1fr_1fr_auto]">
                <Field label={`Abono ${i + 1}: fecha`} type="date" value={r.dueDate} onChange={(e) => setRows((x) => x.map((y, n) => (n === i ? { ...y, dueDate: e.target.value } : y)))} required />
                <Field label={`Importe (${currency})`} type="number" min={0.01} step="0.01" value={r.amount} onChange={(e) => setRows((x) => x.map((y, n) => (n === i ? { ...y, amount: e.target.value } : y)))} required />
                <Button type="button" variant="ghost" aria-label={`Quitar abono ${i + 1}`} disabled={rows.length === 1} onClick={() => setRows((x) => x.filter((_, n) => n !== i))}><Trash2 size={15} aria-hidden /></Button>
              </div>))}
            <p className="text-sm text-ink-soft">Suman {money(rowsTotal)} de {money(d.unscheduled)} por programar.</p>
            <div className="flex justify-end gap-2"><Button type="button" variant="ghost" onClick={() => setMode("view")}>Cancelar</Button><Button type="submit" loading={busy}>Programar</Button></div>
          </form>
        )}

        {mode === "settle" && d && (
          <form noValidate className="flex flex-col gap-3" onSubmit={(e) => {
            e.preventDefault(); const f = new FormData(e.currentTarget);
            if (deductions.some((x) => !x.concept.trim())) { setError("Cada descuento necesita su motivo."); return; }
            if (deductionsTotal > d.custody + 0.001) { setError("Los descuentos no pueden superar lo que se tiene en custodia."); return; }
            post("settle", { deductions: deductions.map((x) => ({ concept: x.concept, amount: Number(x.amount) })), notes: f.get("notes") ?? "" }, "Depósito liquidado.");
          }}>
            <p className="text-sm text-ink-soft">En custodia: {money(d.custody)}. Agrega lo que se retiene, con su motivo.</p>
            {deductions.map((x, i) => (
              <div key={i} className="grid items-end gap-2 sm:grid-cols-[minmax(0,2fr)_1fr_auto]">
                <Field label="Motivo" value={x.concept} maxLength={200} onChange={(e) => setDeductions((v) => v.map((y, n) => (n === i ? { ...y, concept: e.target.value } : y)))} />
                <Field label={`Importe (${currency})`} type="number" min={0.01} step="0.01" value={x.amount} onChange={(e) => setDeductions((v) => v.map((y, n) => (n === i ? { ...y, amount: e.target.value } : y)))} />
                <Button type="button" variant="ghost" aria-label={`Quitar descuento ${i + 1}`} onClick={() => setDeductions((v) => v.filter((_, n) => n !== i))}><Trash2 size={15} aria-hidden /></Button>
              </div>))}
            <div><Button type="button" variant="secondary" onClick={() => setDeductions((v) => [...v, { concept: "", amount: "" }])}><Plus size={15} aria-hidden /> Agregar descuento</Button></div>
            <p className="text-sm font-medium">Se devuelven {money(Math.max(0, d.custody - deductionsTotal))}</p>
            <Textarea label="Notas (opcional)" name="notes" maxLength={1000} />
            <div className="flex justify-end gap-2"><Button type="button" variant="ghost" onClick={() => setMode("view")}>Cancelar</Button><Button type="submit" loading={busy}>Liquidar depósito</Button></div>
          </form>
        )}

        {mode === "void" && (
          <form noValidate className="flex flex-col gap-3" onSubmit={(e) => { e.preventDefault(); post("settlement/void", { reason: new FormData(e.currentTarget).get("reason") }, "Liquidación anulada."); }}>
            <Field label="Motivo de la anulación" name="reason" maxLength={1000} hint="Obligatorio. La liquidación anulada se conserva en el historial." required />
            <div className="flex justify-end gap-2"><Button type="button" variant="ghost" onClick={() => setMode("view")}>Volver</Button><Button type="submit" variant="danger" loading={busy}>Anular liquidación</Button></div>
          </form>
        )}
      </div>
    </Leaf>
  );
}
