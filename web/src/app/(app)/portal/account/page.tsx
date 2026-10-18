"use client";
import { Receipt } from "lucide-react";
import { useState } from "react";
import { ChargeRow } from "@/components/portal-ui";
import { EmptyState, ErrorMessage, Leaf, Pager, Skeleton, cx } from "@/components/ui";
import { api, type Paged, type PortalCharge, type PortalSummary } from "@/lib/api";
import { DownloadButton } from "@/components/download-button";
import { useQuery } from "@/lib/use-query";

export default function AccountPage() {
  const [pendingOnly, setPendingOnly] = useState(true);
  const [page, setPage] = useState(1);
  const currency = useQuery("portal:summary", () => api<PortalSummary>("/portal/summary")).data?.currency ?? "MXN";
  const { data, error } = useQuery(`portal:charges:${pendingOnly}:${page}`, () => api<Paged<PortalCharge>>(`/portal/charges?pendingOnly=${pendingOnly}&page=${page}&pageSize=15`));

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Estado de cuenta</h1>
          <p className="mt-1 text-sm text-ink-soft">Tus cargos y lo que falta por pagar de cada uno.</p>
        </div>
        <DownloadButton path="/portal/statement.pdf">Descargar PDF</DownloadButton>
      </div>
      <div role="tablist" aria-label="Filtro" className="inline-flex self-start rounded-md border border-rule bg-leaf p-0.5">
        {[[true, "Pendientes"], [false, "Todos"]].map(([v, label]) => (
          <button key={String(v)} role="tab" aria-selected={pendingOnly === v} onClick={() => { setPendingOnly(v as boolean); setPage(1); }}
            className={cx("h-8 rounded px-3.5 text-sm font-medium", pendingOnly === v ? "bg-accent-soft text-accent" : "text-ink-soft hover:text-ink")}>{label as string}</button>
        ))}
      </div>
      <ErrorMessage message={error?.message ?? null} />
      <Leaf className="overflow-hidden">
        {!data && !error && <div className="flex flex-col gap-3 p-5">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-10" />)}</div>}
        {data?.items.length === 0 && <EmptyState icon={<Receipt size={18} />} title={pendingOnly ? "No tienes cargos pendientes" : "Aún no hay cargos"}>{pendingOnly ? "Estás al corriente." : "Cuando la administración genere cargos, aparecerán aquí."}</EmptyState>}
        {data && data.items.length > 0 && <ul>{data.items.map((c) => <ChargeRow key={c.id} c={c} currency={currency} />)}</ul>}
        {data && <Pager page={page} pageSize={15} total={data.total} onPage={setPage} />}
      </Leaf>
    </div>
  );
}
