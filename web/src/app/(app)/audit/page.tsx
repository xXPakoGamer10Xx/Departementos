"use client";
import { ChevronDown, Lock, ScrollText } from "lucide-react";
import { useState } from "react";
import { useMe } from "@/components/me-context";
import { Badge, EmptyState, ErrorMessage, Field, Leaf, Pager, Select, Skeleton } from "@/components/ui";
import { api, auditEntityTypes, canManage, formatDateTime, type AuditEntry, type Member, type Paged } from "@/lib/api";
import { useQuery } from "@/lib/use-query";

const PAGE_SIZE = 25;

function Entry({ e }: { e: AuditEntry }) {
  const [open, setOpen] = useState(false);
  const hasDetail = Boolean(e.reason || e.metadata);
  return (
    <li className="border-b border-rule last:border-b-0">
      <button type="button" disabled={!hasDetail} onClick={() => setOpen(!open)} aria-expanded={hasDetail ? open : undefined}
        className="grid w-full items-center gap-x-4 gap-y-1 px-5 py-3 text-left transition-colors enabled:hover:bg-accent-soft/40 md:grid-cols-[170px_minmax(0,1.4fr)_120px_minmax(0,1fr)_16px]">
        <span className="text-xs text-ink-soft">{formatDateTime(e.occurredAt)}</span>
        <span className="min-w-0 text-sm"><span className="font-medium">{e.label}</span><span className="block truncate font-mono text-xs text-ink-faint">{e.action}</span></span>
        <span><Badge>{auditEntityTypes[e.entityType] ?? e.entityType}</Badge></span>
        <span className="truncate text-sm text-ink-soft">{e.actorName ?? "Usuario eliminado"}</span>
        {hasDetail ? <ChevronDown size={16} className={`text-ink-faint transition-transform ${open ? "rotate-180" : ""}`} aria-hidden /> : <span />}
      </button>
      {open && (
        <div className="flex flex-col gap-2 border-t border-rule bg-paper px-5 py-3 text-sm">
          {e.reason && <p><span className="text-ink-soft">Motivo: </span>{e.reason}</p>}
          <p className="font-mono text-xs text-ink-faint">Registro {e.entityType} · {e.entityId}</p>
          {e.metadata && <pre className="overflow-x-auto rounded-md bg-leaf p-3 font-mono text-xs leading-relaxed text-ink-soft">{JSON.stringify(e.metadata, null, 2)}</pre>}
        </div>
      )}
    </li>
  );
}

export default function AuditPage() {
  const me = useMe();
  const admin = canManage(me.activeRole);
  const [entityType, setEntityType] = useState("");
  const [action, setAction] = useState("");
  const [actor, setActor] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [page, setPage] = useState(1);

  const members = useQuery("audit-members", () => (admin ? api<Member[]>("/members") : Promise.resolve(null)));
  const params = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
  if (entityType) params.set("entityType", entityType);
  if (action.trim()) params.set("action", action.trim());
  if (actor) params.set("actorUserId", actor);
  if (from) params.set("from", from);
  if (to) params.set("to", to);
  const qs = params.toString();
  const { data, error, loading } = useQuery(`audit:${qs}`, () => (admin ? api<Paged<AuditEntry>>(`/audit-logs?${qs}`) : Promise.resolve(null)));
  const filtered = Boolean(entityType || action.trim() || actor || from || to);
  const reset = (set: (v: string) => void) => (v: string) => { set(v); setPage(1); };

  if (!admin) {
    return <Leaf><EmptyState icon={<Lock size={18} />} title="No tienes permiso para ver la auditoría">Solo propietarios y administradores pueden consultarla.</EmptyState></Leaf>;
  }

  return (
    <div className="flex flex-col gap-6">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Auditoría</h1>
        <p className="mt-1 text-sm text-ink-soft">Quién hizo qué y cuándo. Los registros son de solo lectura: nadie puede editarlos ni borrarlos.</p>
      </div>

      <Leaf className="overflow-hidden">
        <div className="flex flex-col gap-3 border-b border-rule p-4">
          <div className="grid gap-3 sm:grid-cols-[minmax(0,1fr)_190px_220px]">
            <Field label="" aria-label="Buscar acción" placeholder="Buscar acción, p. ej. payment" value={action} onChange={(e) => reset(setAction)(e.target.value)} />
            <Select aria-label="Tipo de registro" value={entityType} onChange={(e) => reset(setEntityType)(e.target.value)}>
              <option value="">Todos los tipos</option>
              {Object.entries(auditEntityTypes).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </Select>
            <Select aria-label="Persona" value={actor} onChange={(e) => reset(setActor)(e.target.value)}>
              <option value="">Todas las personas</option>
              {members.data?.map((m) => <option key={m.userId} value={m.userId}>{m.fullName ?? m.email}</option>)}
            </Select>
          </div>
          <div className="grid gap-3 sm:grid-cols-[repeat(2,minmax(0,190px))]">
            <Field label="Desde" type="date" value={from} onChange={(e) => reset(setFrom)(e.target.value)} />
            <Field label="Hasta" type="date" value={to} onChange={(e) => reset(setTo)(e.target.value)} />
          </div>
        </div>

        <div aria-hidden className="hidden gap-4 border-b border-rule bg-paper px-5 py-2.5 text-xs font-medium text-ink-soft md:grid md:grid-cols-[170px_minmax(0,1.4fr)_120px_minmax(0,1fr)_16px]">
          <span>Fecha</span><span>Acción</span><span>Tipo</span><span>Persona</span><span />
        </div>
        {error && <div className="p-5"><ErrorMessage message={error.message} /></div>}
        {!data && !error && <div className="flex flex-col gap-3 p-5">{[0, 1, 2, 3].map((i) => <Skeleton key={i} className="h-10" />)}</div>}
        {data && data.items.length === 0 && <EmptyState icon={<ScrollText size={18} />} title={filtered ? "Sin resultados" : "Aún no hay registros"}>{filtered ? "Prueba con otros filtros." : undefined}</EmptyState>}
        {data && data.items.length > 0 && <ul className={loading ? "opacity-60 transition-opacity" : "transition-opacity"}>{data.items.map((e) => <Entry key={e.id} e={e} />)}</ul>}
        {data && <Pager page={data.page} pageSize={data.pageSize} total={data.total} onPage={setPage} />}
      </Leaf>
    </div>
  );
}
