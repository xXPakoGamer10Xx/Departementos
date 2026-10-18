import { Badge } from "@/components/ui";
import { chargeStatusLabels, formatDate, formatMoney, type PortalCharge } from "@/lib/api";
import { chargeTone } from "@/lib/charge-ui";

/** One charge as a tenant reads it: what it is, when it is due, what is left. */
export function ChargeRow({ c, currency }: { c: PortalCharge; currency: string }) {
  return (
    <li className="flex items-start justify-between gap-4 border-b border-rule px-5 py-3.5 last:border-b-0">
      <div className="min-w-0">
        <p className="text-sm font-medium">{c.description}</p>
        <p className="mt-0.5 text-xs text-ink-faint">Vence {formatDate(c.dueDate)}</p>
        {c.paid > 0 && c.balance > 0 && <p className="mt-0.5 text-xs text-ink-soft">Pagaste {formatMoney(c.paid, currency)} de {formatMoney(c.amount, currency)}</p>}
      </div>
      <div className="shrink-0 text-right">
        <p className="font-mono text-sm font-semibold tabular-nums">{formatMoney(c.balance > 0 ? c.balance : c.amount, currency)}</p>
        <div className="mt-1"><Badge tone={chargeTone[c.status]}>{chargeStatusLabels[c.status]}</Badge></div>
      </div>
    </li>
  );
}
