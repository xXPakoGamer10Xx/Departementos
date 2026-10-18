"use client";
import { HandCoins } from "lucide-react";
import { useState } from "react";
import { Badge, EmptyState, ErrorMessage, Leaf, Pager, Skeleton } from "@/components/ui";
import { api, formatDate, formatMoney, paymentMethodLabels, type Paged, type PortalPayment, type PortalSummary } from "@/lib/api";
import { DownloadButton } from "@/components/download-button";
import { useQuery } from "@/lib/use-query";

export default function PortalPaymentsPage() {
  const [page, setPage] = useState(1);
  const currency = useQuery("portal:summary", () => api<PortalSummary>("/portal/summary")).data?.currency ?? "MXN";
  const { data, error } = useQuery(`portal:payments:${page}`, () => api<Paged<PortalPayment>>(`/portal/payments?page=${page}&pageSize=10`));

  return (
    <div className="flex flex-col gap-6">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Mis pagos</h1>
        <p className="mt-1 text-sm text-ink-soft">Los pagos que la administración ha registrado a tu nombre.</p>
      </div>
      <ErrorMessage message={error?.message ?? null} />
      <Leaf className="overflow-hidden">
        {!data && !error && <div className="flex flex-col gap-3 p-5">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-12" />)}</div>}
        {data?.items.length === 0 && <EmptyState icon={<HandCoins size={18} />} title="Aún no hay pagos">Cuando se registre un pago tuyo, lo verás aquí.</EmptyState>}
        {data && data.items.length > 0 && (
          <ul>
            {data.items.map((p) => (
              <li key={p.id} className="border-b border-rule px-5 py-4 last:border-b-0">
                <div className="flex items-start justify-between gap-4">
                  <div>
                    <p className="text-sm font-medium">{formatDate(p.receivedOn)}</p>
                    <p className="mt-0.5 text-xs text-ink-faint">{paymentMethodLabels[p.method]}{p.reference ? ` · ${p.reference}` : ""}</p>
                  </div>
                  <div className="text-right">
                    <p className={`font-mono text-sm font-semibold tabular-nums ${p.status === "Reversed" ? "text-ink-faint line-through" : ""}`}>{formatMoney(p.amount, currency)}</p>
                    {p.status === "Reversed" && <div className="mt-1"><Badge tone="stamp">Revertido</Badge></div>}
                  </div>
                </div>
                {p.receiptFolio && <div className="mt-2"><DownloadButton size="sm" variant="ghost" path={`/portal/payments/${p.id}/receipt.pdf`}>Recibo {p.receiptFolio}</DownloadButton></div>}
                {p.status === "Confirmed" && (p.allocations.length > 0 || p.unassigned > 0) && (
                  <ul className="mt-2 flex flex-col gap-0.5 text-xs text-ink-soft">
                    {p.allocations.map((a, i) => <li key={i} className="flex justify-between gap-4"><span>{a.description}</span><span className="font-mono tabular-nums">{formatMoney(a.amount, currency)}</span></li>)}
                    {p.unassigned > 0 && <li className="flex justify-between gap-4"><span>Saldo a tu favor</span><span className="font-mono tabular-nums">{formatMoney(p.unassigned, currency)}</span></li>}
                  </ul>
                )}
              </li>
            ))}
          </ul>
        )}
        {data && <Pager page={page} pageSize={10} total={data.total} onPage={setPage} />}
      </Leaf>
    </div>
  );
}
