"use client";
import { ChevronRight, Plus, Wrench } from "lucide-react";
import Link from "next/link";
import { useEffect, useState } from "react";
import { useMe } from "@/components/me-context";
import { Badge, EmptyState, ErrorMessage, Leaf, Pager, SearchInput, Select, Skeleton } from "@/components/ui";
import { api, canManage, formatDate, issuePriorityLabels, issueStatusLabels, type Issue, type IssuePriority, type IssueStatus, type Paged, type Property } from "@/lib/api";
import { issueStatusTone, priorityTone } from "@/lib/issue-ui";
import { useQuery } from "@/lib/use-query";

const PAGE_SIZE = 25;

export default function MaintenancePage() {
  const me = useMe();
  const isMaintenance = me.activeRole === "Maintenance";
  const [search, setSearch] = useState("");
  const [debounced, setDebounced] = useState("");
  const [status, setStatus] = useState<"active" | "all" | IssueStatus>("active");
  const [priority, setPriority] = useState<"" | IssuePriority>("");
  const [propertyId, setPropertyId] = useState("");
  const [mine, setMine] = useState(isMaintenance);
  const [page, setPage] = useState(1);

  useEffect(() => {
    const t = setTimeout(() => { setDebounced(search); setPage(1); }, 250);
    return () => clearTimeout(t);
  }, [search]);

  const properties = useQuery("props", () => api<Paged<Property>>("/properties?pageSize=100"));

  const params = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
  if (debounced) params.set("search", debounced);
  if (status === "active") params.set("activeOnly", "true"); else if (status !== "all") params.set("status", status);
  if (priority) params.set("priority", priority);
  if (propertyId) params.set("propertyId", propertyId);
  if (mine) params.set("assignedToMe", "true");
  const qs = params.toString();
  const { data, error, loading } = useQuery(qs, () => api<Paged<Issue>>(`/maintenance-issues?${qs}`));
  const filtered = Boolean(debounced || priority || propertyId || mine || status !== "active");

  return (
    <div className="flex flex-col gap-6">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Mantenimiento</h1>
          <p className="mt-1 text-sm text-ink-soft">Incidencias por propiedad y unidad, de las más urgentes a las menos.</p>
        </div>
        {canManage(me.activeRole) && (
          <Link href="/maintenance/new" className="inline-flex h-10 shrink-0 items-center gap-2 whitespace-nowrap rounded-md bg-accent px-4 text-sm font-medium text-white transition-colors hover:bg-accent-hover"><Plus size={16} aria-hidden /> Nueva incidencia</Link>
        )}
      </div>

      <Leaf className="overflow-hidden">
        <div className="grid gap-3 border-b border-rule p-4 md:grid-cols-[minmax(0,1fr)_150px_140px_180px]">
          <SearchInput label="Buscar incidencias" placeholder="Buscar por título, propiedad o unidad" value={search} onChange={(e) => setSearch(e.target.value)} />
          <Select aria-label="Estado" value={status} onChange={(e) => { setStatus(e.target.value as typeof status); setPage(1); }}>
            <option value="active">Activas</option>
            <option value="all">Todas</option>
            {(Object.keys(issueStatusLabels) as IssueStatus[]).map((s) => <option key={s} value={s}>{issueStatusLabels[s]}</option>)}
          </Select>
          <Select aria-label="Prioridad" value={priority} onChange={(e) => { setPriority(e.target.value as "" | IssuePriority); setPage(1); }}>
            <option value="">Toda prioridad</option>
            {(Object.keys(issuePriorityLabels) as IssuePriority[]).map((p) => <option key={p} value={p}>{issuePriorityLabels[p]}</option>)}
          </Select>
          <Select aria-label="Propiedad" value={propertyId} onChange={(e) => { setPropertyId(e.target.value); setPage(1); }}>
            <option value="">Todas las propiedades</option>
            {properties.data?.items.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
          </Select>
        </div>
        <div className="border-b border-rule px-4 py-2.5">
          <label className="inline-flex cursor-pointer items-center gap-2 text-sm">
            <input type="checkbox" checked={mine} onChange={(e) => { setMine(e.target.checked); setPage(1); }} className="size-4 accent-[var(--color-accent)]" />
            Solo las asignadas a mí
          </label>
        </div>

        {error && <div className="p-5"><ErrorMessage message={error.message} /></div>}
        {!data && !error && <div className="flex flex-col gap-3 p-5">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-14" />)}</div>}
        {data && data.items.length === 0 && (
          <EmptyState icon={<Wrench size={18} />} title={filtered ? "Sin resultados" : "No hay incidencias activas"}>
            {filtered ? "Prueba con otros filtros." : canManage(me.activeRole) ? "Registra una incidencia cuando alguien reporte algo que arreglar." : "Cuando se te asigne una incidencia aparecerá aquí."}
          </EmptyState>
        )}
        {data && data.items.length > 0 && (
          <ul className={loading ? "opacity-60 transition-opacity" : "transition-opacity"}>
            {data.items.map((i) => (
              <li key={i.id} className="border-b border-rule last:border-b-0">
                <Link href={`/maintenance/${i.id}`} className="group grid items-center gap-x-4 gap-y-1.5 px-5 py-3.5 transition-colors hover:bg-accent-soft/40 md:grid-cols-[minmax(0,1.6fr)_90px_110px_minmax(0,1fr)_16px]">
                  <div className="min-w-0">
                    <p className="truncate text-sm font-medium">{i.title}</p>
                    <p className="truncate text-sm text-ink-soft">{i.propertyName}{i.unitCode && <> · <span className="font-mono">{i.unitCode}</span></>} · {formatDate(i.reportedAt.slice(0, 10))}</p>
                  </div>
                  <div><Badge tone={priorityTone[i.priority]}>{issuePriorityLabels[i.priority]}</Badge></div>
                  <div><Badge tone={issueStatusTone[i.status]}>{issueStatusLabels[i.status]}</Badge></div>
                  <p className="truncate text-sm text-ink-soft">{i.assignedToName ?? <span className="text-ink-faint">Sin asignar</span>}</p>
                  <ChevronRight size={16} className="hidden text-ink-faint transition-transform group-hover:translate-x-0.5 md:block" aria-hidden />
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
