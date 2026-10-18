"use client";
import { Receipt } from "lucide-react";
import { Badge, Button, EmptyState, ErrorMessage, Skeleton } from "@/components/ui";
import { api, chargeStatusLabels, formatDate, formatMoney, fromCents, toCents, type Charge, type Paged } from "@/lib/api";
import { chargeTone } from "@/lib/charge-ui";
import { useQuery } from "@/lib/use-query";

/** Open charges of a tenant, oldest first, with an amount to apply to each. The user decides every amount. */
export function ChargeAllocator({ tenantId, currency, values, onChange, available }: {
  tenantId: string; currency: string; values: Record<string, string>; onChange: (v: Record<string, string>) => void; available: number;
}) {
  const { data, error } = useQuery(`open:${tenantId}`, () => api<Paged<Charge>>(`/rent-charges?tenantId=${tenantId}&openOnly=true&pageSize=100`));
  const set = (id: string, v: string) => onChange({ ...values, [id]: v });

  if (error) return <ErrorMessage message={error.message} />;
  if (!data) return <div className="flex flex-col gap-2">{[0, 1].map((i) => <Skeleton key={i} className="h-12" />)}</div>;
  if (data.items.length === 0) {
    return <EmptyState icon={<Receipt size={18} />} title="Este inquilino no tiene cargos pendientes">El pago quedará como saldo a favor y podrás aplicarlo después.</EmptyState>;
  }

  // Fills amounts oldest-first from the payment amount. It only proposes; the user can still edit before confirming.
  function proposeOldestFirst() {
    let left = toCents(available);
    const next: Record<string, string> = {};
    for (const c of data!.items) {
      const take = Math.min(left, toCents(c.balance));
      if (take > 0) { next[c.id] = String(fromCents(take)); left -= take; }
    }
    onChange(next);
  }

  return (
    <div className="flex flex-col gap-3">
      <div className="flex items-center justify-between gap-3">
        <p className="text-sm text-ink-soft">Escribe cuánto aplicar a cada cargo. Lo que no apliques queda como saldo a favor.</p>
        <Button type="button" variant="secondary" size="sm" disabled={available <= 0} onClick={proposeOldestFirst}>Aplicar a los más antiguos</Button>
      </div>
      <ul className="overflow-hidden rounded-md border border-rule">
        {data.items.map((c) => {
          const entered = toCents(values[c.id] ?? "");
          const over = entered > toCents(c.balance);
          return (
            <li key={c.id} className="grid items-center gap-x-4 gap-y-1 border-b border-rule px-4 py-3 last:border-b-0 md:grid-cols-[minmax(0,1.3fr)_120px_110px_150px]">
              <div className="min-w-0">
                <p className="truncate text-sm font-medium">{c.description}</p>
                <p className="truncate text-xs text-ink-soft"><span className="font-mono">{c.unitCode}</span> · {c.propertyName} · vence {formatDate(c.dueDate)}</p>
              </div>
              <div><Badge tone={chargeTone[c.status]}>{chargeStatusLabels[c.status]}</Badge></div>
              <p className="text-sm md:text-right"><span className="text-ink-soft md:hidden">Saldo: </span><span className="font-medium">{formatMoney(c.balance, currency)}</span></p>
              <div className="flex items-center gap-2">
                <input aria-label={`Importe a aplicar a ${c.description}`} type="number" min={0} step="0.01" inputMode="decimal" placeholder="0.00" value={values[c.id] ?? ""}
                  onChange={(e) => set(c.id, e.target.value)} aria-invalid={over}
                  className={`h-9 w-full rounded-md border bg-leaf px-2 text-right text-sm focus:border-accent ${over ? "border-stamp" : "border-rule-strong"}`} />
                <button type="button" className="shrink-0 text-xs text-accent underline" onClick={() => set(c.id, String(c.balance))}>Todo</button>
              </div>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
