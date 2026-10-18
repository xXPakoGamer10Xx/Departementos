import { ApiError } from "@/lib/api";

/** "attachment; filename*=UTF-8''Recibo%20R-000001.pdf" or filename="Recibo R-000001.pdf" → the name, or a fallback. */
function fileNameFrom(header: string | null, fallback: string): string {
  if (!header) return fallback;
  const star = /filename\*=(?:UTF-8'')?([^;]+)/i.exec(header);
  if (star) { try { return decodeURIComponent(star[1].trim().replace(/^"|"$/g, "")); } catch { /* fall through */ } }
  const plain = /filename="?([^";]+)"?/i.exec(header);
  return plain ? plain[1].trim() : fallback;
}

/**
 * Downloads a document from the API and saves it. It goes through fetch (not a plain link) so that a failure, such as the PDF service being down,
 * becomes a message the person can read instead of a page of raw JSON.
 */
export async function downloadFile(path: string, fallbackName = "documento"): Promise<void> {
  const res = await fetch(`/api/v1${path}`, { credentials: "same-origin" });
  if (!res.ok) {
    const problem = await res.json().catch(() => null);
    throw new ApiError(res.status, problem?.detail ?? problem?.title ?? (res.status === 429 ? "Demasiadas descargas seguidas. Espera un momento." : `Error ${res.status}`));
  }
  const blob = await res.blob();
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url; a.download = fileNameFrom(res.headers.get("content-disposition"), fallbackName);
  document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}
