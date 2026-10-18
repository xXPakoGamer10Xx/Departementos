"use client";
import { FileText, Settings } from "lucide-react";
import Link from "next/link";
import { useState } from "react";
import { DownloadButton } from "@/components/download-button";
import { Badge, Button, EmptyState, ErrorMessage, Leaf, Skeleton, SuccessMessage } from "@/components/ui";
import { api, formatDateTime, type ContractTemplate, type LeaseDocument } from "@/lib/api";
import { useQuery } from "@/lib/use-query";

const kb = (n: number) => (n >= 1_048_576 ? `${(n / 1_048_576).toFixed(1)} MB` : `${Math.max(1, Math.round(n / 1024))} KB`);

/**
 * The contracts generated for a lease. Each generation is a new, frozen version (Word and PDF): an earlier version is never overwritten,
 * so the one that was signed can always be found.
 */
export function LeaseDocuments({ leaseId, canGenerate, cancelled }: { leaseId: string; canGenerate: boolean; cancelled: boolean }) {
  const [version, setVersion] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const docs = useQuery(`lease-docs:${leaseId}:${version}`, () => api<LeaseDocument[]>(`/leases/${leaseId}/documents`));
  const template = useQuery("contract-template", () => api<ContractTemplate>("/contract-template").catch((e: { status?: number }) => (e.status === 404 ? null : Promise.reject(e))));

  async function generate() {
    setBusy(true); setError(null); setNotice(null);
    try { const d = await api<LeaseDocument>(`/leases/${leaseId}/documents`, { method: "POST" }); setNotice(`Contrato generado: versión ${d.version}.`); setVersion((v) => v + 1); }
    catch (e) { setError(e instanceof Error ? e.message : "No se pudo generar el contrato."); }
    finally { setBusy(false); }
  }

  const noTemplate = template.data === null;
  return (
    <Leaf className="overflow-hidden">
      <header className="flex flex-wrap items-start justify-between gap-3 px-5 pb-4 pt-5">
        <div>
          <h2 className="text-[15px] font-semibold">Documento del contrato</h2>
          <p className="mt-0.5 text-sm text-ink-soft">Se arma con tu plantilla de Word y los datos de este contrato. Cada vez que lo generas queda una versión nueva; las anteriores no se tocan.</p>
        </div>
        {canGenerate && !cancelled && <Button onClick={generate} loading={busy} disabled={noTemplate}><FileText size={15} aria-hidden /> Generar contrato</Button>}
      </header>
      <div className="perforation" />
      <div className="flex flex-col gap-3 px-5 py-3">
        <SuccessMessage message={notice} />
        <ErrorMessage message={error} />
        {noTemplate && (
          <p className="text-sm text-ink-soft">Aún no hay una plantilla de contrato. {canGenerate
            ? <Link href="/settings/contract" className="inline-flex items-center gap-1 font-medium text-accent hover:underline"><Settings size={13} aria-hidden /> Súbela en Ajustes</Link>
            : "Pídele a un administrador que la suba en Ajustes."}</p>
        )}
      </div>
      {!docs.data && !docs.error && <div className="px-5 pb-5"><Skeleton className="h-12" /></div>}
      {docs.error && <div className="px-5 pb-5"><ErrorMessage message={docs.error.message} /></div>}
      {docs.data?.length === 0 && !noTemplate && <EmptyState icon={<FileText size={18} />} title="Todavía no se ha generado el contrato">{canGenerate && !cancelled ? "Usa «Generar contrato» para crear la versión 1." : "Cuando se genere, aparecerá aquí."}</EmptyState>}
      {docs.data && docs.data.length > 0 && (
        <ul>
          {docs.data.map((d, i) => (
            <li key={d.id} className="flex flex-wrap items-center justify-between gap-3 border-t border-rule px-5 py-3.5">
              <div className="min-w-0">
                <p className="flex items-center gap-2 text-sm font-medium">Versión {d.version}{i === 0 && <Badge tone="accent">Más reciente</Badge>}</p>
                <p className="mt-0.5 text-xs text-ink-faint">{formatDateTime(d.generatedAt)}{d.generatedByName ? ` · ${d.generatedByName}` : ""} · plantilla «{d.templateName}»</p>
                <p className="mt-0.5 font-mono text-[11px] text-ink-faint" title="Huella del PDF (SHA-256): sirve para comprobar que el archivo no cambió">PDF {kb(d.pdfBytes)} · {d.pdfSha256.slice(0, 12)}…</p>
              </div>
              <div className="flex gap-2">
                <DownloadButton size="sm" path={`/leases/${leaseId}/documents/${d.id}/pdf`}>PDF</DownloadButton>
                <DownloadButton size="sm" variant="ghost" path={`/leases/${leaseId}/documents/${d.id}/docx`}>Word</DownloadButton>
              </div>
            </li>
          ))}
        </ul>
      )}
    </Leaf>
  );
}
