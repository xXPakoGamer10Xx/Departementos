"use client";
import { ChevronRight, FileText, Plus } from "lucide-react";
import Link from "next/link";
import { useEffect, useState } from "react";
import { useMe } from "@/components/me-context";
import { Badge, EmptyState, ErrorMessage, Leaf, Pager, SearchInput, Select, Skeleton } from "@/components/ui";
import { api, canManage, canReadLeases, formatMoney, formatPeriod, leaseStatusLabels, type Lease, type LeaseStatus, type Paged } from "@/lib/api";
import { leaseTone } from "@/lib/lease-ui";
import { useQuery } from "@/lib/use-query";

const PAGE_SIZE = 25;
export default function LeasesPage() {
  const me = useMe();
  const admin = canManage(me.activeRole);
  const currency = me.organizations.find((o) => o.id === me.activeOrganizationId)?.currency ?? "MXN";
  const [search, setSearch] = useState("");
  const [debounced, setDebounced] = useState("");
  const [status, setStatus] = useState<"" | LeaseStatus>("");
  const [page, setPage] = useState(1);

  useEffect(() => {
    const t = setTimeout(() => { setDebounced(search); setPage(1); }, 250);
    return () => clearTimeout(t);
  }, [search]);

  const params = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
  if (debounced) params.set("search", debounced);
  if (status) params.set("status", status);
  const qs = params.toString();
  const { data, error, loading } = useQuery(qs, () => api<Paged<Lease>>(`/leases?${qs}`));

  if (!canReadLeases(me.activeRole)) {
    return <Leaf><EmptyState icon={<FileText size={18} />} title="No tienes permiso para ver contratos">Los contratos incluyen importes e identidad de inquilinos, no disponibles para tu rol.</EmptyState></Leaf>;
  }

  return (
    <div className="flex flex-col gap-6">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Contratos</h1>
          <p className="mt-1 text-sm text-ink-soft">Arrendamientos por unidad: vigencia, renta y día de vencimiento.</p>
        </div>
        {admin && <Link href="/leases/new" className="inline-flex h-10 shrink-0 items-center gap-2 whitespace-nowrap rounded-md bg-accent px-4 text-sm font-medium text-white transition-colors hover:bg-accent-hover"><Plus size={16} aria-hidden /> Nuevo contrato</Link>}
      </div>

      <Leaf className="overflow-hidden">
        <div className="grid gap-3 border-b border-rule p-4 sm:grid-cols-[minmax(0,1fr)_200px]">
          <SearchInput label="Buscar contratos" placeholder="Buscar por inquilino, unidad o propiedad" value={search} onChange={(e) => setSearch(e.target.value)} />
          <Select aria-label="Estado del contrato" value={status} onChange={(e) => { setStatus(e.target.value as "" | LeaseStatus); setPage(1); }}>
            <option value="">Todos los estados</option>
            {(Object.keys(leaseStatusLabels) as LeaseStatus[]).map((s) => <option key={s} value={s}>{leaseStatusLabels[s]}</option>)}
          </Select>
        </div>

        {error && <div className="p-5"><ErrorMessage message={error.message} /></div>}
        {!data && !error && <div className="flex flex-col gap-3 p-5">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-14" />)}</div>}
        {data && data.items.length === 0 && (
          <EmptyState icon={<FileText size={18} />} title={debounced || status ? "Sin resultados" : "Aún no hay contratos"}>
            {debounced || status ? "Prueba con otro filtro." : admin ? "Crea el primer contrato eligiendo una unidad y un inquilino." : undefined}
          </EmptyState>
        )}
        {data && data.items.length > 0 && (
          <ul className={loading ? "opacity-60 transition-opacity" : "transition-opacity"}>
            {data.items.map((l) => (
              <li key={l.id} className="border-b border-rule last:border-b-0">
                <Link href={`/leases/${l.id}`} className="group grid items-center gap-x-4 gap-y-1 px-5 py-3.5 transition-colors hover:bg-accent-soft/40 md:grid-cols-[minmax(0,1.3fr)_minmax(0,1.2fr)_110px_132px]">
                  <div className="min-w-0">
                    <p className="truncate font-medium"><span className="font-mono">{l.unitCode}</span> · {l.propertyName}</p>
                    <p className="truncate text-sm text-ink-soft">{l.tenantName}</p>
                  </div>
                  <p className="text-sm text-ink-soft">{formatPeriod(l)}</p>
                  <p className="text-sm font-medium md:text-right">{formatMoney(l.monthlyRent, currency)}</p>
                  <div className="flex items-center justify-between gap-2">
                    <Badge tone={leaseTone[l.status]}>{leaseStatusLabels[l.status]}</Badge>
                    <ChevronRight size={16} className="text-ink-faint transition-transform group-hover:translate-x-0.5" aria-hidden />
                  </div>
                </Link>
              </li>
            ))}
          </ul>
        )}
        {data && <Pager page={data.page} pageSize={data.pageSize} total={data.total} onPage={setPage} />}
      </Leaf>
    </div>
  );
}
