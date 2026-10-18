"use client";
import { Download } from "lucide-react";
import { useState, type ReactNode } from "react";
import { Button, ErrorMessage } from "@/components/ui";
import { downloadFile } from "@/lib/download";

/** A button that downloads a document and tells the person if it could not be made. */
export function DownloadButton({ path, children, variant = "secondary", size = "md", fallbackName }: {
  path: string; children: ReactNode; variant?: "primary" | "secondary" | "ghost"; size?: "sm" | "md"; fallbackName?: string;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  return (
    <span className="inline-flex flex-col items-start gap-2">
      <Button type="button" variant={variant} size={size} loading={busy} onClick={async () => {
        setBusy(true); setError(null);
        try { await downloadFile(path, fallbackName); } catch (e) { setError(e instanceof Error ? e.message : "No se pudo descargar."); }
        finally { setBusy(false); }
      }}>{!busy && <Download size={size === "sm" ? 14 : 15} aria-hidden />}{children}</Button>
      <ErrorMessage message={error} />
    </span>
  );
}
