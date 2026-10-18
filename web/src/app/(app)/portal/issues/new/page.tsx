"use client";
import { ArrowLeft } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { Button, ErrorMessage, Field, Leaf, Textarea } from "@/components/ui";
import { api, type PortalIssue } from "@/lib/api";

export default function NewPortalIssuePage() {
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    setBusy(true); setError(null);
    try {
      const issue = await api<PortalIssue>("/portal/issues", { body: { title: f.get("title"), description: f.get("description") } });
      router.push(`/portal/issues/${issue.id}`);
    } catch (err) { setError(err instanceof Error ? err.message : "No se pudo enviar el reporte."); setBusy(false); }
  }

  return (
    <div className="flex flex-col gap-6">
      <div>
        <Link href="/portal/issues" className="inline-flex items-center gap-1 text-sm text-ink-soft hover:text-ink"><ArrowLeft size={14} aria-hidden /> Mis reportes</Link>
        <h1 className="mt-2 text-2xl font-semibold tracking-tight">Nuevo reporte</h1>
        <p className="mt-1 text-sm text-ink-soft">Cuéntanos qué pasa. Lo atiende la administración y verás aquí cómo avanza.</p>
      </div>
      <Leaf>
        <form onSubmit={submit} className="flex flex-col gap-5 p-5">
          <Field label="¿Qué pasó?" name="title" maxLength={200} required placeholder="Ej. Fuga de agua en el baño" />
          <Textarea label="Detalles" name="description" rows={5} maxLength={4000} hint="Dónde ocurre, desde cuándo, qué has notado." />
          <ErrorMessage message={error} />
          <div><Button type="submit" loading={busy}>Enviar reporte</Button></div>
        </form>
      </Leaf>
    </div>
  );
}
