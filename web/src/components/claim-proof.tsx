import { ExternalLink } from "lucide-react";
import type { PaymentClaim } from "@/lib/api";

const kb = (n: number) => (n >= 1_048_576 ? `${(n / 1_048_576).toFixed(1)} MB` : `${Math.max(1, Math.round(n / 1024))} KB`);

/** The proof a tenant attached. Images show inline (same origin, signed-in cookie); a PDF opens as a download. */
export function ClaimProof({ file }: { file: PaymentClaim["file"] }) {
  const href = `/api/v1/files/${file.id}`;
  return file.contentType.startsWith("image/") ? (
    <a href={href} target="_blank" rel="noreferrer" className="block w-fit" aria-label={`Abrir comprobante ${file.name}`}>
      {/* eslint-disable-next-line @next/next/no-img-element -- a private, authenticated file; next/image would fetch it without the cookie */}
      <img src={href} alt={`Comprobante: ${file.name}`} className="max-h-48 rounded-md border border-rule object-contain" />
    </a>
  ) : (
    <a href={href} className="inline-flex items-center gap-1.5 text-sm font-medium text-accent hover:underline">
      <ExternalLink size={14} aria-hidden /> {file.name} <span className="font-normal text-ink-faint">({kb(file.sizeBytes)})</span>
    </a>
  );
}
