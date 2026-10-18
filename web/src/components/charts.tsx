"use client";
import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import { Leaf } from "@/components/ui";

/* Chart colors. Marks carry the data color; every label, value and legend wears a text token (never the series color).
   Validated with the dataviz palette script on the white leaf surface: accent vs context gray ΔE 29.8, contrast ≥ 3:1;
   the red ramp is an ordinal ramp (monotone lightness, one hue). */
export const chartColors = {
  accent: "#1d3fb8",        // the series the story is about
  context: "#84958e",       // de-emphasis gray for the series that gives context
  agingRamp: ["#e8a39d", "#d4675e", "#b3261e", "#7a1712"], // 1–30 → 91+ days late
  urgent: "#b3261e", high: "#855400", normal: "#84958e", low: "#aebcb6",
} as const;

/** Card with a title, an optional legend slot and a "table view" twin of the chart (the accessible equivalent of the picture). */
export function ChartCard({ title, description, legend, action, table, children, empty }: {
  title: string; description: string; legend?: ReactNode; action?: ReactNode; table: { head: string[]; rows: (string | number)[][] }; children: ReactNode; empty?: ReactNode;
}) {
  const [asTable, setAsTable] = useState(false);
  return (
    <Leaf className="flex flex-col">
      <header className="flex items-start justify-between gap-3 px-5 py-4">
        <div>
          <h2 className="text-[15px] font-semibold">{title}</h2>
          <p className="mt-0.5 text-sm text-ink-soft">{description}</p>
        </div>
        <div className="flex shrink-0 items-center gap-4">
          {action}
          {!empty && <button type="button" onClick={() => setAsTable(!asTable)} aria-pressed={asTable} className="text-sm font-medium text-accent hover:underline">{asTable ? "Ver gráfica" : "Ver tabla"}</button>}
        </div>
      </header>
      <div className="perforation" />
      <div className="flex flex-1 flex-col gap-3 px-5 pb-5 pt-4">
        {empty ?? (asTable ? (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead><tr className="border-b border-rule text-xs text-ink-soft">{table.head.map((h, i) => <th key={h} className={`py-2 font-medium ${i === 0 ? "text-left" : "text-right"}`}>{h}</th>)}</tr></thead>
              <tbody>{table.rows.map((r, ri) => <tr key={ri} className="border-b border-rule last:border-b-0">{r.map((c, i) => <td key={i} className={`py-2 tabular-nums ${i === 0 ? "text-left" : "text-right"}`}>{c}</td>)}</tr>)}</tbody>
            </table>
          </div>
        ) : (<>{legend}{children}</>))}
      </div>
    </Leaf>
  );
}

export function LegendItem({ color, label }: { color: string; label: string }) {
  return <span className="inline-flex items-center gap-1.5 text-xs text-ink-soft"><span className="size-2.5 rounded-sm" style={{ background: color }} aria-hidden />{label}</span>;
}

/** Floating readout. The value leads (strong), the name follows (secondary); a short line key identifies each series. */
type TipRow = { color?: string; value: string; label: string };
type Tip = { x: number; y: number; title: string; rows: TipRow[]; note?: string };
function TooltipBox({ tip }: { tip: Tip | null }) {
  if (!tip) return null;
  return (
    <div role="status" className="pointer-events-none absolute z-10 min-w-40 -translate-x-1/2 -translate-y-full rounded-md border border-rule-strong bg-leaf px-3 py-2 text-xs"
      style={{ left: tip.x, top: tip.y - 8 }}>
      <p className="mb-1 font-medium capitalize text-ink-soft">{tip.title}</p>
      {tip.rows.map((r) => (
        <p key={r.label} className="flex items-center gap-2">
          {r.color && <span className="inline-block h-0.5 w-3 rounded" style={{ background: r.color }} aria-hidden />}
          <strong className="font-semibold tabular-nums text-ink">{r.value}</strong><span className="text-ink-soft">{r.label}</span>
        </p>
      ))}
      {tip.note && <p className="mt-1 text-ink-faint">{tip.note}</p>}
    </div>
  );
}

/** Round the axis maximum up to a clean number (1, 2, 2.5, 5 × 10^k) so ticks land on tidy values. */
function niceMax(max: number): number {
  if (max <= 0) return 1;
  const exp = 10 ** Math.floor(Math.log10(max));
  for (const m of [1, 2, 2.5, 5, 10]) if (max <= m * exp) return m * exp;
  return 10 * exp;
}

export type ColumnGroup = { key: string; label: string; title: string; a: number; b: number; note?: string };

/**
 * Grouped columns: for each month, "a" (context, gray) next to "b" (the story, accent). One shared zero baseline and a single axis.
 * Columns are 20px thick with a 4px rounded top and a square base, separated by a 2px surface gap. Only the latest "b" is labeled; the
 * rest of the values live in the tooltip (hover or keyboard focus) and in the table view.
 */
export function GroupedColumns({ groups, aName, bName, format, compact, aColor = chartColors.context, bColor = chartColors.accent }: {
  groups: ColumnGroup[]; aName: string; bName: string; format: (n: number) => string; compact: (n: number) => string; aColor?: string; bColor?: string;
}) {
  const id = useId();
  const box = useRef<HTMLDivElement>(null);
  const [tip, setTip] = useState<Tip | null>(null);
  const [active, setActive] = useState<string | null>(null);

  // The drawing is sized to its container (1 unit = 1 CSS px), so text keeps its size on phones instead of shrinking with the picture.
  const [W, setW] = useState(640);
  useEffect(() => {
    const el = box.current;
    if (!el) return;
    const ro = new ResizeObserver(() => setW(Math.max(260, Math.round(el.clientWidth))));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  const H = 250, left = W < 420 ? 44 : 54, right = 8, top = 18, bottom = 26;
  const pw = W - left - right, ph = H - top - bottom;
  const max = niceMax(Math.max(...groups.flatMap((g) => [g.a, g.b])));
  const y = (v: number) => top + ph - (v / max) * ph;
  const band = pw / groups.length, gap = 2;
  const barW = Math.max(8, Math.min(20, Math.floor((band - 12) / 2 - gap)));
  const ticks = [0, 1, 2, 3, 4].map((i) => (max / 4) * i);

  // 4px rounded data end (the top), square where the column sits on the baseline.
  const column = (x: number, v: number) => {
    const h = top + ph - y(v);
    if (h <= 0) return null;
    const r = Math.min(4, h);
    return `M${x},${top + ph} V${y(v) + r} Q${x},${y(v)} ${x + r},${y(v)} H${x + barW - r} Q${x + barW},${y(v)} ${x + barW},${y(v) + r} V${top + ph} Z`;
  };

  const show = (g: ColumnGroup, cx: number) => {
    setActive(g.key);
    setTip({ x: Math.min(Math.max(cx, 90), W - 90), y: Math.min(y(g.a), y(g.b)), title: g.title, note: g.note, rows: [{ color: bColor, value: format(g.b), label: bName }, { color: aColor, value: format(g.a), label: aName }] });
  };
  const hide = () => { setActive(null); setTip(null); };

  return (
    <div ref={box} className="relative">
      <svg width={W} height={H} viewBox={`0 0 ${W} ${H}`} className="block max-w-full" role="group" aria-labelledby={`${id}-desc`}>
        <desc id={`${id}-desc`}>{`${bName} frente a ${aName} por mes: ` + groups.map((g) => `${g.title}, ${format(g.b)} y ${format(g.a)}`).join("; ")}</desc>
        {ticks.map((t) => (
          <g key={t}>
            <line x1={left} x2={W - right} y1={y(t)} y2={y(t)} stroke="var(--color-rule)" strokeWidth={1} />
            <text x={left - 8} y={y(t) + 4} textAnchor="end" fontSize={11} fill="var(--color-ink-faint)" style={{ fontVariantNumeric: "tabular-nums" }}>{compact(t)}</text>
          </g>
        ))}
        {groups.map((g, i) => {
          const cx = left + band * i + band / 2;
          const ax = cx - barW - gap / 2, bx = cx + gap / 2;
          const last = i === groups.length - 1;
          return (
            <g key={g.key} onPointerMove={() => show(g, cx)} onPointerLeave={hide} onFocus={() => show(g, cx)} onBlur={hide} tabIndex={0}
              aria-label={`${g.title}: ${bName} ${format(g.b)}, ${aName} ${format(g.a)}`} className="outline-none [&:focus-visible_.hit]:stroke-[var(--color-accent)]">
              <rect className="hit" x={left + band * i + 4} y={top} width={band - 8} height={ph + bottom - 2} rx={6} fill={active === g.key ? "var(--color-paper)" : "transparent"} stroke="transparent" strokeWidth={2} />
              {column(ax, g.a) && <path d={column(ax, g.a)!} fill={aColor} opacity={active && active !== g.key ? 0.55 : 1} />}
              {column(bx, g.b) && <path d={column(bx, g.b)!} fill={bColor} opacity={active && active !== g.key ? 0.55 : 1} />}
              {last && g.b > 0 && <text x={bx + barW / 2} y={y(g.b) - 6} textAnchor="middle" fontSize={11} fontWeight={600} fill="var(--color-ink)">{compact(g.b)}</text>}
              <text x={cx} y={H - 8} textAnchor="middle" fontSize={12} fill="var(--color-ink-soft)">{g.label}</text>
            </g>
          );
        })}
      </svg>
      <TooltipBox tip={tip} />
    </div>
  );
}

export type BarRow = { key: string; label: string; value: number; valueText: string; color: string; detail?: string; tipTitle: string; tipRows: TipRow[] };

/**
 * Horizontal bars (one baseline on the left). 16px thick with a 4px rounded data end; the value sits at the tip.
 * Used for ordered buckets (a one-hue ramp) and for priorities.
 */
export function HBars({ rows, labelWidth = 112 }: { rows: BarRow[]; labelWidth?: number }) {
  const box = useRef<HTMLDivElement>(null);
  const [tip, setTip] = useState<Tip | null>(null);
  const max = Math.max(...rows.map((r) => r.value), 0);
  const show = (r: BarRow, el: HTMLElement) => {
    const b = box.current?.getBoundingClientRect(), e = el.getBoundingClientRect();
    if (b) setTip({ x: Math.min(Math.max(e.left - b.left + Math.min(e.width, 220) / 2, 90), b.width - 90), y: e.top - b.top, title: r.tipTitle, rows: r.tipRows });
  };
  return (
    <div ref={box} className="relative">
      <ul className="flex flex-col gap-2.5">
        {rows.map((r) => (
          <li key={r.key} tabIndex={0} aria-label={`${r.label}: ${r.valueText}`} className="rounded-md outline-none focus-visible:ring-2 focus-visible:ring-accent"
            onPointerMove={(e) => show(r, e.currentTarget)} onPointerLeave={() => setTip(null)} onFocus={(e) => show(r, e.currentTarget)} onBlur={() => setTip(null)}>
            <div className="grid items-center gap-3" style={{ gridTemplateColumns: `${labelWidth}px minmax(0,1fr)` }}>
              <span className="truncate text-sm text-ink-soft">{r.label}</span>
              <span className="flex items-center gap-2">
                <span className="block h-4 rounded-r-[4px] transition-opacity" style={{ width: max === 0 ? 0 : `max(${r.value > 0 ? 3 : 0}px, ${(r.value / max) * 78}%)`, background: r.color }} aria-hidden />
                <span className="whitespace-nowrap text-sm font-semibold tabular-nums">{r.valueText}</span>
                {r.detail && <span className="whitespace-nowrap text-xs text-ink-faint">{r.detail}</span>}
              </span>
            </div>
          </li>
        ))}
      </ul>
      <TooltipBox tip={tip} />
    </div>
  );
}
