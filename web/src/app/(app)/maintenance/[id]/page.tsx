"use client";
import { ArrowLeft, CheckCircle2, CircleDot, MessageSquare, Pencil, PlusCircle, UserPlus, Wrench } from "lucide-react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useState, type ReactNode } from "react";
import { useMe } from "@/components/me-context";
import { Badge, Button, EmptyState, ErrorMessage, Field, Leaf, Select, Skeleton, SuccessMessage, Textarea } from "@/components/ui";
import {
  api, canActOnIssue, canManage, formatDateTime, issuePriorityLabels, issueStatusLabels, nextStatuses, roleLabels,
  type Issue, type IssueEvent, type IssuePriority, type IssueStatus, type Member, type Paged, type Unit,
} from "@/lib/api";
import { issueStatusTone, priorityTone } from "@/lib/issue-ui";
import { useQuery } from "@/lib/use-query";

const priorities = Object.keys(issuePriorityLabels) as IssuePriority[];
const actionLabel = (from: IssueStatus, to: IssueStatus): string =>
  to === "Resolved" ? "Marcar como resuelta" : to === "Closed" ? "Cerrar" : to === "Open" ? "Devolver a abierta" : to === "InProgress" ? (from === "Resolved" ? "Reabrir" : "Poner en proceso") : "Poner en espera";

function EventRow({ e, tenantIssue }: { e: IssueEvent; tenantIssue: boolean }) {
  const who = <strong className="font-medium">{e.actorName ?? "Alguien"}</strong>;
  const icon = e.type === "Comment" ? <MessageSquare size={14} /> : e.type === "StatusChanged" ? (e.toStatus === "Resolved" ? <CheckCircle2 size={14} /> : <CircleDot size={14} />)
    : e.type === "Assigned" ? <UserPlus size={14} /> : e.type === "Updated" ? <Pencil size={14} /> : <PlusCircle size={14} />;
  let text: ReactNode;
  switch (e.type) {
    case "Created": text = <>{who} creó la incidencia</>; break;
    case "StatusChanged": text = <>{who} cambió el estado de <em className="not-italic text-ink-soft">{e.fromStatus ? issueStatusLabels[e.fromStatus] : "—"}</em> a <strong className="font-medium">{e.toStatus ? issueStatusLabels[e.toStatus] : "—"}</strong></>; break;
    case "Assigned": text = e.targetName ? <>{who} asignó a <strong className="font-medium">{e.targetName}</strong></> : <>{who} quitó la asignación</>; break;
    case "Updated": text = <>{who} editó la incidencia</>; break;
    case "Comment": text = <>{who} comentó</>; break;
  }
  return (
    <li className="flex gap-3 px-5 py-3.5">
      <span className="mt-0.5 flex size-6 shrink-0 items-center justify-center rounded-full bg-paper-deep text-ink-soft" aria-hidden>{icon}</span>
      <div className="min-w-0 text-sm">
        <p>{text} <span className="text-ink-faint">· {formatDateTime(e.createdAt)}</span>{tenantIssue && e.type === "Comment" && (e.tenantVisible ? <span className="ml-2 align-middle"><Badge tone="accent">Lo ve el inquilino</Badge></span> : <span className="ml-2 align-middle"><Badge>Interno</Badge></span>)}</p>
        {e.body && (e.type === "Comment"
          ? <p className="mt-1.5 whitespace-pre-wrap rounded-md bg-paper px-3 py-2 text-ink">{e.body}</p>
          : <p className="mt-0.5 whitespace-pre-wrap text-ink-soft">{e.body}</p>)}
      </div>
    </li>
  );
}

export default function IssueDetailPage() {
  const { id } = useParams<{ id: string }>();
  const me = useMe();
  const admin = canManage(me.activeRole);

  const [version, setVersion] = useState(0);
  const [panel, setPanel] = useState<"edit" | null>(null);
  const [moving, setMoving] = useState<IssueStatus | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const { data: issue, error: loadError } = useQuery(`i:${id}:${version}`, () => api<Issue>(`/maintenance-issues/${id}`));
  const members = useQuery(`m:${admin}`, () => (admin ? api<Member[]>("/members") : Promise.resolve(null)));
  const units = useQuery(`u:${issue?.propertyId ?? "none"}`, () => (admin && issue ? api<Paged<Unit>>(`/properties/${issue.propertyId}/units?pageSize=100`) : Promise.resolve(null)));

  async function run(action: () => Promise<unknown>, success: string, after?: () => void) {
    setBusy(true); setError(null); setNotice(null);
    try { await action(); setNotice(success); after?.(); setVersion((v) => v + 1); }
    catch (e) { setError(e instanceof Error ? e.message : "No se pudo completar la acción."); }
    finally { setBusy(false); }
  }

  if (loadError) {
    return (
      <Leaf><EmptyState icon={<Wrench size={18} />} title={loadError.status === 404 ? "Incidencia no encontrada" : "No se pudo cargar la incidencia"}>
        {loadError.status === 404 ? "Puede no existir o no pertenecer a tu organización." : loadError.message}
        <Link href="/maintenance" className="mt-3 block font-medium text-accent hover:underline">Volver a mantenimiento</Link>
      </EmptyState></Leaf>
    );
  }

  const next = issue ? nextStatuses(issue, me.activeRole, me.user.id) : [];
  const canWrite = issue ? canActOnIssue(issue, me.activeRole, me.user.id) : false;
  const assignees = (members.data ?? []).filter((m) => m.status === "Active" && (m.role === "Maintenance" || m.role === "Admin" || m.role === "Owner"));

  return (
    <div className="flex flex-col gap-6">
      <div>
        <Link href="/maintenance" className="mb-3 inline-flex items-center gap-1.5 text-sm text-ink-soft hover:text-ink"><ArrowLeft size={14} aria-hidden /> Mantenimiento</Link>
        {!issue ? <Skeleton className="h-16 w-96" /> : (
          <div className="flex flex-wrap items-start justify-between gap-4">
            <div className="min-w-0">
              <div className="flex flex-wrap items-center gap-3">
                <h1 className="text-2xl font-semibold tracking-tight">{issue.title}</h1>
                <Badge tone={priorityTone[issue.priority]}>{issuePriorityLabels[issue.priority]}</Badge>
                <Badge tone={issueStatusTone[issue.status]}>{issueStatusLabels[issue.status]}</Badge>
                {issue.reportedByTenant && <Badge tone="accent">Reportada por el inquilino</Badge>}
              </div>
              <p className="mt-1 text-sm text-ink-soft">
                <Link href={`/properties/${issue.propertyId}`} className="font-medium text-accent hover:underline">{issue.propertyName}</Link>
                {issue.unitCode && <> · unidad <span className="font-mono">{issue.unitCode}</span></>} · reportada el {formatDateTime(issue.reportedAt)}
              </p>
            </div>
            {admin && issue.status !== "Closed" && <Button variant="secondary" onClick={() => setPanel(panel === "edit" ? null : "edit")}><Pencil size={15} aria-hidden /> Editar</Button>}
          </div>
        )}
      </div>

      <ErrorMessage message={error} />
      <SuccessMessage message={notice} />

      {issue && panel === "edit" && (
        <Leaf>
          <header className="px-5 pb-4 pt-5"><h2 className="text-[15px] font-semibold">Editar incidencia</h2></header>
          <div className="perforation" />
          <form noValidate className="flex flex-col gap-4 px-5 pb-5 pt-4" onSubmit={(e) => {
            e.preventDefault(); const f = new FormData(e.currentTarget);
            run(() => api(`/maintenance-issues/${id}`, { method: "PATCH", body: { title: f.get("title"), description: f.get("description") ?? "", priority: f.get("priority"), unitId: f.get("unitId") || undefined, clearUnit: !f.get("unitId") } }),
              "Incidencia actualizada.", () => setPanel(null));
          }}>
            <Field label="Título" name="title" defaultValue={issue.title} maxLength={200} required />
            <Textarea label="Descripción" name="description" defaultValue={issue.description} maxLength={4000} />
            <div className="grid gap-4 sm:grid-cols-2">
              <Select label="Prioridad" name="priority" defaultValue={issue.priority}>{priorities.map((p) => <option key={p} value={p}>{issuePriorityLabels[p]}</option>)}</Select>
              <Select label="Unidad" name="unitId" defaultValue={issue.unitId ?? ""}>
                <option value="">Toda la propiedad</option>
                {units.data?.items.filter((u) => u.status !== "Archived" || u.id === issue.unitId).map((u) => <option key={u.id} value={u.id}>{u.code}</option>)}
              </Select>
            </div>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" onClick={() => setPanel(null)}>Cancelar</Button>
              <Button type="submit" loading={busy}>Guardar cambios</Button>
            </div>
          </form>
        </Leaf>
      )}

      {issue && (
        <Leaf className="overflow-hidden">
          <header className="px-5 py-4"><h2 className="text-[15px] font-semibold">Descripción</h2></header>
          <div className="perforation" />
          <p className="whitespace-pre-wrap px-5 py-4 text-sm text-ink-soft">{issue.description || "Sin descripción."}</p>
          <dl className="grid divide-y divide-rule border-t border-rule sm:grid-cols-3 sm:divide-x sm:divide-y-0">
            <div className="px-5 py-3"><dt className="text-xs text-ink-soft">Asignada a</dt><dd className="mt-0.5 text-sm font-medium">{issue.assignedToName ?? <span className="font-normal text-ink-faint">Sin asignar</span>}</dd></div>
            <div className="px-5 py-3"><dt className="text-xs text-ink-soft">Reportada por</dt><dd className="mt-0.5 text-sm font-medium">{issue.createdByName ?? "—"}</dd></div>
            <div className="px-5 py-3"><dt className="text-xs text-ink-soft">Resuelta</dt><dd className="mt-0.5 text-sm font-medium">{issue.resolvedAt ? `${formatDateTime(issue.resolvedAt)} · ${issue.resolvedByName ?? ""}` : <span className="font-normal text-ink-faint">Pendiente</span>}</dd></div>
          </dl>
        </Leaf>
      )}

      {issue && (next.length > 0 || (admin && issue.status !== "Closed")) && (
        <Leaf>
          <header className="px-5 py-4"><h2 className="text-[15px] font-semibold">Acciones</h2></header>
          <div className="perforation" />
          <div className="flex flex-col gap-4 px-5 pb-5 pt-4">
            {next.length > 0 && (
              <div className="flex flex-wrap gap-2">
                {next.map((s) => (
                  <Button key={s} variant={s === "Resolved" ? "primary" : "secondary"} onClick={() => setMoving(moving === s ? null : s)}>{actionLabel(issue.status, s)}</Button>
                ))}
              </div>
            )}
            {moving && (
              <form noValidate className="grid items-end gap-3 rounded-md bg-paper p-4 sm:grid-cols-[minmax(0,1fr)_auto]" onSubmit={(e) => {
                e.preventDefault(); const note = String(new FormData(e.currentTarget).get("note") ?? "").trim();
                run(() => api(`/maintenance-issues/${id}/status`, { body: { status: moving, note: note || null } }), `Estado: ${issueStatusLabels[moving]}.`, () => setMoving(null));
              }}>
                <Field label={`${actionLabel(issue.status, moving)}: nota (opcional)`} name="note" maxLength={2000} placeholder={moving === "Resolved" ? "Qué se hizo" : moving === "Waiting" ? "Qué se está esperando" : ""} />
                <div className="flex gap-2"><Button type="button" variant="ghost" onClick={() => setMoving(null)}>Cancelar</Button><Button type="submit" loading={busy}>Confirmar</Button></div>
              </form>
            )}
            {admin && issue.status !== "Closed" && (
              <form className="grid items-end gap-3 sm:grid-cols-[minmax(0,1fr)_auto]" onSubmit={(e) => {
                e.preventDefault(); const uid = String(new FormData(e.currentTarget).get("assignee") ?? "");
                run(() => api(`/maintenance-issues/${id}/assign`, { body: { userId: uid || null } }), uid ? "Incidencia asignada." : "Asignación quitada.");
              }}>
                <Select label="Asignar a" name="assignee" defaultValue={issue.assignedToUserId ?? ""} key={`${issue.assignedToUserId}-${members.data?.length ?? 0}`}>
                  <option value="">Sin asignar</option>
                  {assignees.map((m) => <option key={m.userId} value={m.userId}>{m.fullName ?? m.email} · {roleLabels[m.role]}</option>)}
                </Select>
                <Button type="submit" variant="secondary" loading={busy}><UserPlus size={15} aria-hidden /> Guardar asignación</Button>
              </form>
            )}
          </div>
        </Leaf>
      )}

      {issue && (
        <Leaf className="overflow-hidden">
          <header className="px-5 py-4"><h2 className="text-[15px] font-semibold">Historial</h2></header>
          <div className="perforation" />
          <ul className="divide-y divide-rule">{(issue.events ?? []).map((e) => <EventRow key={e.id} e={e} tenantIssue={!!issue.reportedByTenant} />)}</ul>
          {canWrite ? (
            <form noValidate className="grid items-end gap-3 border-t border-rule bg-paper px-5 py-4 sm:grid-cols-[minmax(0,1fr)_auto]" onSubmit={(e) => {
              e.preventDefault(); const form = e.currentTarget; const data = new FormData(form); const body = String(data.get("body") ?? "");
              run(() => api(`/maintenance-issues/${id}/comments`, { body: { body, visibleToTenant: data.get("visibleToTenant") === "on" } }), "Comentario agregado.", () => form.reset());
            }}>
              <Field label="Agregar comentario" name="body" maxLength={2000} placeholder="Escribe una actualización…" required />
              <Button type="submit" loading={busy}>Comentar</Button>
              {issue.reportedByTenant && (
                <label className="flex items-center gap-2 text-sm text-ink-soft sm:col-span-2">
                  <input type="checkbox" name="visibleToTenant" className="size-4 accent-[var(--color-accent)]" /> Que lo vea el inquilino que reportó esto
                </label>
              )}
            </form>
          ) : issue.status === "Closed" ? (
            <p className="border-t border-rule bg-paper px-5 py-3 text-sm text-ink-soft">La incidencia está cerrada y ya no admite cambios.</p>
          ) : me.activeRole === "Maintenance" ? (
            <p className="border-t border-rule bg-paper px-5 py-3 text-sm text-ink-soft">Solo puedes comentar y actualizar las incidencias que tienes asignadas.</p>
          ) : null}
        </Leaf>
      )}
    </div>
  );
}
