"use client";
import { BarChart3, Download, Lock } from "lucide-react";
import { useState } from "react";
import { useMe } from "@/components/me-context";
import { EmptyState, ErrorMessage, Field, Leaf, Select, Skeleton } from "@/components/ui";
import { api, canReadReports, chargeStatusLabels, currentMonthValue, formatMoney, formatMonth, monthBounds, monthValueAgo, type ChargeStatus, type FinancialSummary, type PaymentStatus, paymentStatusLabels } from "@/lib/api";
import { useQuery } from "@/lib/use-query";

const DownloadLink = ({ href, children, hint }: { href: string; children: string; hint: string }) => (
  <a href={href} download className="group flex items-center justify-between gap-4 rounded-md border border-rule-strong bg-leaf px-4 py-3 transition-colors hover:border-accent hover:bg-accent-soft/40">
    <span><span className="block text-sm font-medium">{children}</span><span className="block text-xs text-ink-soft">{hint}</span></span>
    <Download size={16} className="shrink-0 text-ink-soft group-hover:text-accent" aria-hidden />
  </a>
);

export default function ReportsPage() {
  const me = useMe();
  const [from, setFrom] = useState(monthValueAgo(5));
  const [to, setTo] = useState(currentMonthValue());
  const [chargeStatus, setChargeStatus] = useState<"" | ChargeStatus>("");
  const [paymentStatus, setPaymentStatus] = useState<"" | PaymentStatus>("");
  const currency = me.organizations.find((o) => o.id === me.activeOrganizationId)?.currency ?? "MXN";

  const a = monthBounds(from); const b = monthBounds(to);
  const range = a && b ? { from: a.from, to: b.to } : null;
  const invalid = !range || range.from > range.to;
  const { data, error, loading } = useQuery(`rep:${range?.from}:${range?.to}`, () => (range && !invalid ? api<FinancialSummary>(`/reports/financial-summary?from=${range.from}&to=${range.to}`) : Promise.resolve(null)));

  if (!canReadReports(me.activeRole)) {
    return <Leaf><EmptyState icon={<Lock size={18} />} title="No tienes permiso para ver reportes">La información financiera no está disponible para tu rol.</EmptyState></Leaf>;
  }

  const q = range ? `from=${range.from}&to=${range.to}` : "";
  const rows = data?.byMonth ?? [];
  const sum = (pick: (r: (typeof rows)[number]) => number) => rows.reduce((t, r) => t + pick(r), 0);
  const m = (n: number) => formatMoney(n, currency);

  return (
    <div className="flex flex-col gap-6">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Reportes</h1>
        <p className="mt-1 text-sm text-ink-soft">Cargos por periodo, pagos recibidos por fecha de recepción y saldos pendientes. Moneda: {currency}.</p>
      </div>

      <Leaf>
        <div className="grid gap-4 p-5 sm:grid-cols-[repeat(2,minmax(0,200px))_1fr]">
          <Field label="Desde" type="month" value={from} onChange={(e) => e.target.value && setFrom(e.target.value)} />
          <Field label="Hasta" type="month" value={to} onChange={(e) => e.target.value && setTo(e.target.value)} />
        </div>
        {invalid && range && <p role="alert" className="border-t border-rule px-5 py-3 text-sm text-stamp">El mes inicial no puede ser posterior al final.</p>}
      </Leaf>

      <ErrorMessage message={error?.message ?? null} />
      <Leaf className="overflow-hidden">
        <header className="px-5 py-4"><h2 className="text-[15px] font-semibold">Mes por mes</h2>
          <p className="mt-0.5 text-sm text-ink-soft">Lo cargado frente a lo recibido. Los pagos revertidos van aparte y no suman a lo recibido.</p></header>
        <div className="perforation" />
        {!data && !error && !invalid && <div className="p-5"><Skeleton className="h-40" /></div>}
        {data && !data.byMonth && <p className="px-5 py-5 text-sm text-ink-soft">El rango es muy largo para el desglose mensual (máximo 24 meses); los totales sí se calculan.</p>}
        {data?.byMonth && (
          <div className={`overflow-x-auto ${loading ? "opacity-60" : ""}`}>
            <table className="w-full min-w-[640px] text-sm">
              <thead>
                <tr className="border-b border-rule bg-paper text-xs font-medium text-ink-soft">
                  <th className="px-5 py-2.5 text-left">Mes</th><th className="px-3 py-2.5 text-right">Renta cargada</th><th className="px-3 py-2.5 text-right">Otros cargos</th>
                  <th className="px-3 py-2.5 text-right">Recibido</th><th className="px-5 py-2.5 text-right">Revertido</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r) => (
                  <tr key={`${r.year}-${r.month}`} className="border-b border-rule">
                    <td className="px-5 py-2.5 capitalize">{formatMonth(r.year, r.month)}</td>
                    <td className="px-3 py-2.5 text-right tabular-nums">{m(r.rentCharged)}</td>
                    <td className="px-3 py-2.5 text-right tabular-nums">{m(r.otherCharged)}</td>
                    <td className="px-3 py-2.5 text-right font-medium tabular-nums">{m(r.received)}</td>
                    <td className={`px-5 py-2.5 text-right tabular-nums ${r.reversed > 0 ? "text-ink-soft" : "text-ink-faint"}`}>{m(r.reversed)}</td>
                  </tr>
                ))}
              </tbody>
              <tfoot>
                <tr className="bg-paper font-semibold">
                  <td className="px-5 py-2.5">Total</td><td className="px-3 py-2.5 text-right tabular-nums">{m(sum((r) => r.rentCharged))}</td><td className="px-3 py-2.5 text-right tabular-nums">{m(sum((r) => r.otherCharged))}</td>
                  <td className="px-3 py-2.5 text-right tabular-nums">{m(sum((r) => r.received))}</td><td className="px-5 py-2.5 text-right tabular-nums text-ink-soft">{m(sum((r) => r.reversed))}</td>
                </tr>
              </tfoot>
            </table>
          </div>
        )}
        {data && (
          <p className="border-t border-rule bg-paper px-5 py-3 text-sm text-ink-soft">
            Saldo pendiente total a la fecha: <strong className="text-ink">{m(data.pendingBalance)}</strong> · vencido: <strong className={data.overdueBalance > 0 ? "text-stamp" : "text-ink"}>{m(data.overdueBalance)}</strong>
            <br />Depósitos en custodia (no son ingreso): <strong className="text-ink">{m(data.depositsHeld)}</strong> · devueltos en el periodo: <strong className="text-ink">{m(data.depositsReturned)}</strong> · retenidos: <strong className="text-ink">{m(data.depositsRetained)}</strong>
          </p>
        )}
      </Leaf>

      <Leaf>
        <header className="px-5 py-4">
          <h2 className="flex items-center gap-2 text-[15px] font-semibold"><BarChart3 size={16} aria-hidden /> Exportar a CSV</h2>
          <p className="mt-0.5 text-sm text-ink-soft">Se abren en Excel con acentos correctos. Incluyen solo lo que tu rol puede ver, y cada descarga queda en la auditoría.</p>
        </header>
        <div className="perforation" />
        <div className="grid gap-4 px-5 pb-5 pt-4 md:grid-cols-3">
          <div className="flex flex-col gap-2">
            <Select aria-label="Estado de los cargos" value={chargeStatus} onChange={(e) => setChargeStatus(e.target.value as "" | ChargeStatus)}>
              <option value="">Todos los estados</option>
              {(Object.keys(chargeStatusLabels) as ChargeStatus[]).map((s) => <option key={s} value={s}>{chargeStatusLabels[s]}</option>)}
            </Select>
            <DownloadLink href={`/api/v1/reports/charges.csv?${q}${chargeStatus ? `&status=${chargeStatus}` : ""}`} hint="Un renglón por cargo del periodo, con pagado y saldo.">Cargos del periodo</DownloadLink>
          </div>
          <div className="flex flex-col gap-2">
            <Select aria-label="Estado de los pagos" value={paymentStatus} onChange={(e) => setPaymentStatus(e.target.value as "" | PaymentStatus)}>
              <option value="">Confirmados y revertidos</option>
              {(Object.keys(paymentStatusLabels) as PaymentStatus[]).map((s) => <option key={s} value={s}>{paymentStatusLabels[s]}</option>)}
            </Select>
            <DownloadLink href={`/api/v1/reports/payments.csv?${q}${paymentStatus ? `&status=${paymentStatus}` : ""}`} hint="Pagos por fecha de recepción, con aplicado y saldo a favor.">Pagos recibidos</DownloadLink>
          </div>
          <div className="flex flex-col justify-end gap-2">
            <DownloadLink href="/api/v1/reports/balances.csv" hint="Cargos con saldo a la fecha, con días de atraso.">Saldos pendientes y vencidos</DownloadLink>
          </div>
        </div>
      </Leaf>
    </div>
  );
}
