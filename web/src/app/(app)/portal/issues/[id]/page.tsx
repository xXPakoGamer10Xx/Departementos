"use client";
import { ArrowLeft, CircleDot, MessageSquare, PlusCircle } from "lucide-react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useState } from "react";
import { Badge, Button, ErrorMessage, Field, Leaf, Skeleton } from "@/components/ui";
import { api, formatDateTime, issueStatusLabels, type PortalIssue } from "@/lib/api";
import { issueStatusTone } from "@/lib/issue-ui";
import { useQuery } from "@/lib/use-query";

export default function PortalIssuePage() {
  const { id } = useParams<{ id: string }>();
  const [version, setVersion] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const { data: issue, error: loadError } = useQuery(`portal:issue:${id}:${version}`, () => api<PortalIssue>(`/portal/issues/${id}`));

  async function comment(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const form = e.currentTarget;
    setBusy(true); setError(null);
    try { await api(`/portal/issues/${id}/comments`, { body: { body: new FormData(form).get("body") } }); form.reset(); setVersion((v) => v + 1); }
    catch (err) { setError(err instanceof Error ? err.message : "No se pudo enviar."); }
    finally { setBusy(false); }
  }

  return (
    <div className="flex flex-col gap-6">
      <Link href="/portal/issues" className="inline-flex items-center gap-1 text-sm text-ink-soft hover:text-ink"><ArrowLeft size={14} aria-hidden /> Mis reportes</Link>
      <ErrorMessage message={loadError?.status === 404 ? "No encontramos este reporte." : (loadError?.message ?? null)} />
      {!issue && !loadError && <Skeleton className="h-16 w-80" />}
      {issue && (
        <>
          <div>
            <div className="flex flex-wrap items-center gap-3">
              <h1 className="text-2xl font-semibold tracking-tight">{issue.title}</h1>
              <Badge tone={issueStatusTone[issue.status]}>{issueStatusLabels[issue.status]}</Badge>
            </div>
            <p className="mt-1 text-sm text-ink-soft">Reportado el {formatDateTime(issue.reportedAt)}{issue.unitCode && <> · unidad <span className="font-mono">{issue.unitCode}</span></>}</p>
            {issue.description && <p className="mt-4 whitespace-pre-wrap text-sm">{issue.description}</p>}
          </div>
          <Leaf className="overflow-hidden">
            <header className="px-5 py-4"><h2 className="text-[15px] font-semibold">Seguimiento</h2></header>
            <div className="perforation" />
            <ul className="divide-y divide-rule">
              {(issue.events ?? []).map((e, i) => (
                <li key={i} className="flex gap-3 px-5 py-3.5">
                  <span className="mt-0.5 flex size-6 shrink-0 items-center justify-center rounded-full bg-paper-deep text-ink-soft" aria-hidden>
                    {e.type === "Comment" ? <MessageSquare size={14} /> : e.type === "StatusChanged" ? <CircleDot size={14} /> : <PlusCircle size={14} />}
                  </span>
                  <div className="min-w-0 text-sm">
                    <p>
                      {e.type === "Created" && "Enviaste el reporte"}
                      {e.type === "StatusChanged" && <>El estado cambió a <strong className="font-medium">{e.toStatus ? issueStatusLabels[e.toStatus] : "—"}</strong></>}
                      {e.type === "Comment" && (e.fromTenant ? "Tú escribiste" : "La administración escribió")}
                      <span className="text-ink-faint"> · {formatDateTime(e.createdAt)}</span>
                    </p>
                    {e.type === "Comment" && e.body && <p className="mt-1.5 whitespace-pre-wrap rounded-md bg-paper px-3 py-2">{e.body}</p>}
                  </div>
                </li>
              ))}
            </ul>
            {issue.status !== "Closed" ? (
              <form onSubmit={comment} className="grid items-end gap-3 border-t border-rule bg-paper px-5 py-4 sm:grid-cols-[minmax(0,1fr)_auto]">
                <Field label="Agregar un mensaje" name="body" maxLength={2000} required placeholder="Escribe aquí…" />
                <Button type="submit" loading={busy}>Enviar</Button>
                {error && <div className="sm:col-span-2"><ErrorMessage message={error} /></div>}
              </form>
            ) : <p className="border-t border-rule bg-paper px-5 py-3 text-sm text-ink-soft">Este reporte está cerrado. Si el problema sigue, crea uno nuevo.</p>}
          </Leaf>
        </>
      )}
    </div>
  );
}
