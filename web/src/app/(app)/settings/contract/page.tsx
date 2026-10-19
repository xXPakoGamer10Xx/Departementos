"use client";
import { ArrowLeft, Copy, FileText, Lock, Upload } from "lucide-react";
import Link from "next/link";
import { useState } from "react";
import { DownloadButton } from "@/components/download-button";
import { useMe } from "@/components/me-context";
import { Badge, Button, EmptyState, ErrorMessage, Leaf, Skeleton, SuccessMessage } from "@/components/ui";
import { ApiError, api, canManage, canReadLeases, formatDateTime, type ContractField, type ContractTemplate } from "@/lib/api";
import { useQuery } from "@/lib/use-query";

const MAX_BYTES = 5 * 1024 * 1024;

export default function ContractTemplatePage() {
  const me = useMe();
  const allowed = canReadLeases(me.activeRole);
  const admin = canManage(me.activeRole);
  const [version, setVersion] = useState(0);
  const [file, setFile] = useState<File | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [copied, setCopied] = useState<string | null>(null);

  const active = useQuery(`tpl:${version}`, () => (allowed ? api<ContractTemplate>("/contract-template").catch((e: { status?: number }) => (e.status === 404 ? null : Promise.reject(e))) : Promise.resolve(undefined)));
  const history = useQuery(`tpl-history:${version}`, () => (allowed ? api<ContractTemplate[]>("/contract-template/history") : Promise.resolve([])));
  const fields = useQuery("tpl-fields", () => (allowed ? api<ContractField[]>("/contract-template/fields") : Promise.resolve([])));

  async function upload(e: React.FormEvent) {
    e.preventDefault();
    if (!file) { setError("Elige el archivo .docx de tu contrato."); return; }
    if (file.size > MAX_BYTES) { setError("El archivo pesa más de 5 MB."); return; }
    setBusy(true); setError(null); setNotice(null);
    try {
      const body = new FormData(); body.append("file", file);
      const res = await fetch("/api/v1/contract-template", { method: "POST", body, credentials: "same-origin" });
      if (!res.ok) { const p = await res.json().catch(() => null); throw new ApiError(res.status, p?.detail ?? p?.title ?? "No se pudo subir la plantilla."); }
      setNotice("Plantilla guardada. Los contratos que generes desde ahora usarán esta."); setFile(null); setVersion((v) => v + 1);
    } catch (err) { setError(err instanceof Error ? err.message : "No se pudo subir la plantilla."); }
    finally { setBusy(false); }
  }

  if (!allowed) {
    return <Leaf><EmptyState icon={<Lock size={18} />} title="No tienes permiso para ver esta sección">La plantilla de contrato no está disponible para tu rol.</EmptyState></Leaf>;
  }
  const t = active.data;

  return (
    <div className="flex flex-col gap-6">
      <div>
        <Link href="/settings" className="mb-3 inline-flex items-center gap-1.5 text-sm text-ink-soft hover:text-ink"><ArrowLeft size={14} aria-hidden /> Ajustes</Link>
        <h1 className="text-2xl font-semibold tracking-tight">Plantilla de contrato</h1>
        <p className="mt-1 text-sm text-ink-soft">Tu contrato en Word, con campos como <code className="rounded bg-paper-deep px-1 font-mono text-xs">{"{{nombre_completo}}"}</code> que NethRent llena con los datos de cada contrato.</p>
      </div>
      <SuccessMessage message={notice} />
      <ErrorMessage message={error ?? active.error?.message ?? null} />

      <Leaf>
        <header className="px-5 pb-4 pt-5"><h2 className="text-[15px] font-semibold">Plantilla actual</h2></header>
        <div className="perforation" />
        {t === undefined && !active.error && <div className="p-5"><Skeleton className="h-16" /></div>}
        {t === null && <EmptyState icon={<FileText size={18} />} title="Aún no hay plantilla">Sube tu contrato en Word. Si no tienes uno a la mano, descarga el ejemplo para ver los campos disponibles.</EmptyState>}
        {t && (
          <div className="flex flex-col gap-3 px-5 py-4">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <div><p className="text-sm font-medium">{t.name}</p><p className="text-xs text-ink-faint">Subida el {formatDateTime(t.createdAt)} · {t.fields.length} campos</p></div>
              <DownloadButton size="sm" path={`/contract-template/${t.id}/file`} fallbackName="plantilla.docx">Descargar</DownloadButton>
            </div>
            <div className="flex flex-wrap gap-1.5">{t.fields.map((f) => <Badge key={f} tone="neutral"><span className="font-mono">{f}</span></Badge>)}</div>
            {t.missingRecommended.length > 0 && (
              <p className="rounded-md bg-pending-soft px-3 py-2 text-sm text-pending">Tu plantilla no usa {t.missingRecommended.map((f) => `{{${f}}}`).join(", ")}. Si el contrato debe decir eso, agrégalo.</p>
            )}
          </div>
        )}
        {admin && (
          <>
            <div className="perforation" />
            <form onSubmit={upload} noValidate className="flex flex-col gap-3 px-5 py-4">
              <div className="flex flex-col gap-1.5">
                <label htmlFor="tpl" className="text-sm font-medium">{t ? "Reemplazar la plantilla" : "Subir tu plantilla"}</label>
                <input id="tpl" type="file" accept=".docx,application/vnd.openxmlformats-officedocument.wordprocessingml.document" onChange={(e) => { setFile(e.target.files?.[0] ?? null); setError(null); }}
                  className="block w-full text-sm file:mr-3 file:h-10 file:rounded-md file:border file:border-rule-strong file:bg-leaf file:px-4 file:text-sm file:font-medium hover:file:bg-paper" />
                <p className="text-xs text-ink-faint">Archivo .docx de hasta 5 MB, sin macros. La anterior se conserva en el historial; los contratos ya generados no cambian.</p>
              </div>
              <div className="flex flex-wrap gap-2">
                <Button type="submit" loading={busy}><Upload size={15} aria-hidden /> Subir plantilla</Button>
                <DownloadButton path="/contract-template/sample" fallbackName="Plantilla de ejemplo.docx">Descargar ejemplo con todos los campos</DownloadButton>
              </div>
            </form>
          </>
        )}
      </Leaf>

      <Leaf className="overflow-hidden">
        <header className="px-5 pb-4 pt-5"><h2 className="text-[15px] font-semibold">Campos disponibles</h2><p className="mt-0.5 text-sm text-ink-soft">Escríbelos en tu Word entre dobles llaves. Si un dato no existe (por ejemplo, no hay fiador), su renglón se quita solo, junto con su título si se queda solo.</p></header>
        <div className="perforation" />
        {!fields.data && !fields.error && <div className="p-5"><Skeleton className="h-20" /></div>}
        {fields.data && (
          <ul className="divide-y divide-rule">
            {fields.data.map((f) => (
              <li key={f.name} className="grid items-start gap-x-4 gap-y-1 px-5 py-3 sm:grid-cols-[230px_minmax(0,1fr)]">
                <button type="button" title="Copiar" onClick={async () => { await navigator.clipboard.writeText(`{{${f.name}}}`).catch(() => {}); setCopied(f.name); setTimeout(() => setCopied((c) => (c === f.name ? null : c)), 1500); }}
                  className="inline-flex w-fit items-center gap-1.5 rounded bg-paper-deep px-2 py-0.5 font-mono text-xs hover:bg-accent-soft">
                  {`{{${f.name}}}`} <Copy size={11} aria-hidden /><span className="sr-only">Copiar</span>{copied === f.name && <span className="font-sans text-ledger">copiado</span>}
                </button>
                <p className="text-sm text-ink-soft">{f.description}{f.example && <span className="block text-xs text-ink-faint">Ejemplo: {f.example}</span>}</p>
              </li>
            ))}
          </ul>
        )}
      </Leaf>

      {history.data && history.data.length > 1 && (
        <Leaf className="overflow-hidden">
          <header className="px-5 pb-4 pt-5"><h2 className="text-[15px] font-semibold">Historial</h2></header>
          <div className="perforation" />
          <ul className="divide-y divide-rule">
            {history.data.map((h) => (
              <li key={h.id} className="flex flex-wrap items-center justify-between gap-3 px-5 py-3">
                <span className="text-sm">{h.name} <span className="text-xs text-ink-faint">· {formatDateTime(h.createdAt)}</span> {h.isActive && <Badge tone="accent">Vigente</Badge>}</span>
                <DownloadButton size="sm" variant="ghost" path={`/contract-template/${h.id}/file`} fallbackName="plantilla.docx">Descargar</DownloadButton>
              </li>
            ))}
          </ul>
        </Leaf>
      )}
    </div>
  );
}
