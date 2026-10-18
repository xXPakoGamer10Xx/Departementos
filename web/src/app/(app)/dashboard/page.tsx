"use client";
import { CheckCircle2 } from "lucide-react";
import Link from "next/link";
import { useState, type ReactNode } from "react";
import { useMe } from "@/components/me-context";
import { ChartCard, GroupedColumns, HBars, LegendItem, chartColors } from "@/components/charts";
import { ErrorMessage, Field, Leaf, Skeleton } from "@/components/ui";
import {
  activityHref, api, currentMonthValue, formatCompactMoney, formatDate, formatDateTime, formatMoney, formatMonth, issuePriorityLabels, monthShort, parseMonthValue,
  type Dashboard, type FinancialSummary, type IssuePriority,
} from "@/lib/api";
import { useQuery } from "@/lib/use-query";

/** One line of the statement: what it is, how it is defined, and the figure. Every indicator states its own definition. */
function Line({ label, definition, value, sub, tone }: { label: string; definition: string; value: string; sub?: string; tone?: "alert" | "faint" }) {
  return (
    <div className="grid items-baseline gap-x-6 gap-y-1 px-5 py-3.5 sm:grid-cols-[minmax(0,1fr)_auto]">
      <div className="min-w-0">
        <dt className="text-sm font-medium">{label}</dt>
        <dd className="mt-0.5 max-w-xl text-xs text-ink-soft">{definition}</dd>
      </div>
      <p className={`text-right text-base font-semibold tabular-nums ${tone === "alert" ? "text-stamp" : tone === "faint" ? "text-ink-soft" : ""}`}>
        {value}{sub && <span className="block text-xs font-normal text-ink-soft">{sub}</span>}
      </p>
    </div>
  );
}

const Group = ({ title, children }: { title: string; children: ReactNode }) => (
  <>
    <div className="border-y border-rule bg-paper px-5 py-2 text-xs font-medium text-ink-soft first:border-t-0">{title}</div>
    <dl className="divide-y divide-rule">{children}</dl>
  </>
);

function Statement({ f, period, currency }: { f: FinancialSummary; period: string; currency: string }) {
  const m = (n: number) => formatMoney(n, currency);
  const count = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;
  return (
    <Leaf className="overflow-hidden">
      <header className="px-5 py-4">
        <h2 className="text-[15px] font-semibold">Estado de cuenta</h2>
        <p className="mt-0.5 text-sm text-ink-soft">Periodo {period} · {currency}. Las cifras “a la fecha” son al {formatDate(f.asOf)}.</p>
      </header>
      <div className="perforation" />
      <Group title={`En el periodo (${period})`}>
        <Line label="Ingresos recibidos" definition="Pagos confirmados cuya fecha de recepción cae dentro del periodo."
          value={m(f.paymentsReceived.total)} sub={count(f.paymentsReceived.count, "pago", "pagos")} />
        <Line label="Cargos de renta generados" definition="Rentas mensuales cargadas a contratos para meses del periodo, sin contar las anuladas."
          value={m(f.rentChargesGenerated.total)} sub={count(f.rentChargesGenerated.count, "cargo", "cargos")} />
        <Line label="Otros cargos generados" definition="Cuotas, reposiciones y demás cargos manuales del periodo, sin anulados."
          value={m(f.otherChargesGenerated.total)} sub={count(f.otherChargesGenerated.count, "cargo", "cargos")} />
        <Line label="Pagos revertidos" definition="Pagos con fecha de recepción en el periodo que después se revirtieron. No cuentan como ingreso."
          value={m(f.paymentsReversed.total)} sub={count(f.paymentsReversed.count, "pago", "pagos")} tone="faint" />
        <Line label="Cargos anulados" definition="Cargos del periodo que se anularon con motivo. No cuentan como generados."
          value={m(f.chargesVoided.total)} sub={count(f.chargesVoided.count, "cargo", "cargos")} tone="faint" />
      </Group>
      <Group title="A la fecha">
        <Line label="Saldo pendiente total" definition="Todo lo cargado menos lo aplicado por pagos, en cualquier periodo. No depende del periodo elegido."
          value={m(f.pendingBalance)} sub={count(f.openCharges, "cargo abierto", "cargos abiertos")} />
        <Line label="Cargos vencidos" definition="Cargos con saldo cuya fecha de vencimiento ya pasó, según la fecha local de la organización."
          value={m(f.overdueBalance)} sub={count(f.overdueCharges, "cargo", "cargos")} tone={f.overdueCharges > 0 ? "alert" : undefined} />
        <Line label="Inquilinos con adeudo vencido" definition="Inquilinos distintos que tienen al menos un cargo vencido con saldo."
          value={String(f.tenantsWithOverdue)} tone={f.tenantsWithOverdue > 0 ? "alert" : undefined} />
        <Line label="Saldo a favor" definition="Dinero recibido en pagos confirmados que todavía no se aplicó a ningún cargo." value={m(f.unassignedCredit)} />
      </Group>
    </Leaf>
  );
}

function FinanceCharts({ f, currency }: { f: FinancialSummary; currency: string }) {
  const m = (n: number) => formatMoney(n, currency);
  const compact = (n: number) => formatCompactMoney(n, currency);
  const trend = f.byMonth ?? [];
  const groups = trend.map((r) => ({
    key: `${r.year}-${r.month}`, label: monthShort(r.year, r.month), title: formatMonth(r.year, r.month), a: r.rentCharged + r.otherCharged, b: r.received,
    note: r.reversed > 0 ? `Revertidos: ${m(r.reversed)} (no cuentan como recibido)` : undefined,
  }));
  const last = trend[trend.length - 1];
  const upTo = last ? formatMonth(last.year, last.month) : "";
  const hasTrend = groups.some((g) => g.a > 0 || g.b > 0 || g.note);

  const aging = f.overdueAging;
  const agingRows = aging.map((b, i) => ({
    key: b.label, label: b.label, value: b.balance, valueText: compact(b.balance), color: chartColors.agingRamp[i], detail: `${b.count} ${b.count === 1 ? "cargo" : "cargos"}`,
    tipTitle: b.label, tipRows: [{ value: m(b.balance), label: "saldo vencido" }, { value: String(b.count), label: b.count === 1 ? "cargo" : "cargos" }],
  }));

  return (
    <div className="grid gap-6 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
      <ChartCard title="Cargado frente a recibido" description={`Últimos 6 meses hasta ${upTo}. Cargado = renta y otros cargos del mes; recibido = pagos por fecha de recepción.`}
        legend={<div className="flex gap-4"><LegendItem color={chartColors.accent} label="Recibido" /><LegendItem color={chartColors.context} label="Cargado" /></div>}
        table={{ head: ["Mes", "Cargado", "Recibido", "Revertido"], rows: trend.map((r) => [formatMonth(r.year, r.month), m(r.rentCharged + r.otherCharged), m(r.received), m(r.reversed)]) }}
        empty={hasTrend ? undefined : <p className="py-10 text-center text-sm text-ink-soft">Aún no hay cargos ni pagos en estos 6 meses.</p>}>
        <GroupedColumns groups={groups} aName="Cargado" bName="Recibido" format={m} compact={compact} />
      </ChartCard>

      <ChartCard title="Antigüedad del adeudo vencido" description={`Saldo vencido según los días de atraso, al ${formatDate(f.asOf)}. Más oscuro, más atrasado.`}
        table={{ head: ["Antigüedad", "Cargos", "Saldo vencido"], rows: aging.map((b) => [b.label, b.count, m(b.balance)]) }}
        empty={f.overdueCharges === 0 ? <p className="flex items-center justify-center gap-2 py-10 text-sm text-ink-soft"><CheckCircle2 size={16} className="text-ledger" aria-hidden /> No hay adeudo vencido.</p> : undefined}>
        <HBars rows={agingRows} labelWidth={104} />
        <p className="border-t border-rule pt-3 text-sm text-ink-soft">Total vencido: <strong className="text-stamp">{m(f.overdueBalance)}</strong> · {f.overdueCharges} {f.overdueCharges === 1 ? "cargo" : "cargos"} de {f.tenantsWithOverdue} {f.tenantsWithOverdue === 1 ? "inquilino" : "inquilinos"}</p>
      </ChartCard>
    </div>
  );
}

export default function DashboardPage() {
  const me = useMe();
  const [month, setMonth] = useState(currentMonthValue());
  const p = parseMonthValue(month);
  const { data, error, loading } = useQuery(`dash:${month}`, () => api<Dashboard>(`/dashboard${p ? `?year=${p.year}&month=${p.month}` : ""}`));
  const periodLabel = p ? formatMonth(p.year, p.month) : "";
  const occ = data?.occupancy;
  const priorityRows = (["Urgent", "High", "Normal", "Low"] as IssuePriority[]).map((pr) => {
    const n = data ? data.issues[pr.toLowerCase() as "urgent" | "high" | "normal" | "low"] : 0;
    return { key: pr, label: issuePriorityLabels[pr], value: n, valueText: String(n), color: { Urgent: chartColors.urgent, High: chartColors.high, Normal: chartColors.normal, Low: chartColors.low }[pr],
      tipTitle: `Prioridad ${issuePriorityLabels[pr].toLowerCase()}`, tipRows: [{ value: String(n), label: n === 1 ? "incidencia abierta" : "incidencias abiertas" }] };
  });

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Resumen</h1>
          <p className="mt-1 text-sm text-ink-soft">{me.organizations.find((o) => o.id === me.activeOrganizationId)?.name}{data ? ` · ${data.timeZone}` : ""}</p>
        </div>
        {me.activeRole !== "Maintenance" && <div className="w-48"><Field label="Periodo" type="month" value={month} onChange={(e) => e.target.value && setMonth(e.target.value)} /></div>}
      </div>

      <ErrorMessage message={error?.message ?? null} />
      {!data && !error && <div className="flex flex-col gap-4"><Skeleton className="h-72" /><Skeleton className="h-40" /></div>}

      {data && (
        <div className={loading ? "flex flex-col gap-6 opacity-60 transition-opacity" : "flex flex-col gap-6 transition-opacity"}>
          {data.finance && <FinanceCharts f={data.finance} currency={data.currency} />}
          {data.finance && <Statement f={data.finance} period={periodLabel} currency={data.currency} />}

          <div className="grid gap-6 lg:grid-cols-2">
            <Leaf>
              <header className="px-5 py-4">
                <h2 className="text-[15px] font-semibold">Ocupación</h2>
                <p className="mt-0.5 text-sm text-ink-soft">Unidades ocupadas entre las unidades activas (no archivadas), al {formatDate(data.asOf)}.</p>
              </header>
              <div className="perforation" />
              <div className="flex flex-col gap-4 px-5 pb-5 pt-4">
                {occ && occ.totalUnits === 0 ? (
                  <p className="text-sm text-ink-soft">Aún no hay unidades activas. <Link href="/properties" className="font-medium text-accent hover:underline">Registra una propiedad</Link>.</p>
                ) : occ && (
                  <>
                    <p className="text-sm"><span className="text-2xl font-semibold tabular-nums">{Math.round(occ.occupancyRate * 100)}%</span> <span className="text-ink-soft">· {occ.occupied} de {occ.totalUnits} unidades ocupadas</span></p>
                    <div role="img" aria-label={`${occ.occupied} ocupadas, ${occ.available} disponibles, ${occ.maintenance} en mantenimiento`} className="flex h-3 gap-0.5 overflow-hidden rounded-full bg-leaf">
                      <span className="bg-ledger" style={{ width: `${(occ.occupied / occ.totalUnits) * 100}%` }} />
                      <span className="bg-rule-strong" style={{ width: `${(occ.available / occ.totalUnits) * 100}%` }} />
                      <span className="bg-pending" style={{ width: `${(occ.maintenance / occ.totalUnits) * 100}%` }} />
                    </div>
                    <ul className="grid gap-1.5 text-sm">
                      <li className="flex items-center justify-between"><span className="inline-flex items-center gap-2"><span className="size-2.5 rounded-sm bg-ledger" aria-hidden />Ocupadas</span><span className="tabular-nums">{occ.occupied}</span></li>
                      <li className="flex items-center justify-between"><span className="inline-flex items-center gap-2"><span className="size-2.5 rounded-sm bg-rule-strong" aria-hidden />Disponibles</span><span className="tabular-nums">{occ.available}</span></li>
                      <li className="flex items-center justify-between"><span className="inline-flex items-center gap-2"><span className="size-2.5 rounded-sm bg-pending" aria-hidden />En mantenimiento</span><span className="tabular-nums">{occ.maintenance}</span></li>
                    </ul>
                  </>
                )}
              </div>
            </Leaf>

            <ChartCard title="Incidencias abiertas" description="Abiertas, en proceso o en espera, por prioridad."
              action={<Link href="/maintenance" className="text-sm font-medium text-accent hover:underline">Ver todas</Link>}
              table={{ head: ["Prioridad", "Abiertas"], rows: priorityRows.map((r) => [r.label, r.value]) }}
              empty={data.issues.total === 0 ? <p className="flex items-center justify-center gap-2 py-8 text-sm text-ink-soft"><CheckCircle2 size={16} className="text-ledger" aria-hidden /> No hay incidencias abiertas.</p> : undefined}>
              <HBars rows={priorityRows} labelWidth={84} />
            </ChartCard>
          </div>

          <Leaf className="overflow-hidden">
            <header className="px-5 py-4"><h2 className="text-[15px] font-semibold">Actividad reciente</h2></header>
            <div className="perforation" />
            {data.activity.length === 0 && <p className="px-5 py-5 text-sm text-ink-soft">Todavía no hay actividad.</p>}
            <ul className="divide-y divide-rule">
              {data.activity.map((a) => {
                const href = activityHref(a);
                const row = (
                  <span className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-0.5 px-5 py-3 text-sm">
                    <span><strong className="font-medium">{a.actorName ?? "Alguien"}</strong> <span className="text-ink-soft">{a.label.charAt(0).toLowerCase() + a.label.slice(1)}</span></span>
                    <span className="text-xs text-ink-faint">{formatDateTime(a.occurredAt)}</span>
                  </span>
                );
                return <li key={a.id}>{href ? <Link href={href} className="block transition-colors hover:bg-accent-soft/40">{row}</Link> : row}</li>;
              })}
            </ul>
          </Leaf>
        </div>
      )}
    </div>
  );
}
