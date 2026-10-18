"use client";
import { Banknote, Check, Copy, FileCheck2, QrCode, Upload } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { QrImage } from "@/components/qr";
import { Badge, Button, EmptyState, ErrorMessage, Field, Leaf, Pager, Select, Skeleton, SuccessMessage, Textarea, type Tone } from "@/components/ui";
import {
  ApiError, api, claimStatusLabels, formatDate, formatDateTime, formatMoney, paymentMethodLabels, reportableMethods, todayIso,
  type CashCode, type Paged, type PaymentMethod, type PortalPaymentInfo, type PortalSummary, type TenantClaim,
} from "@/lib/api";
import { useQuery } from "@/lib/use-query";

const claimTone: Record<TenantClaim["status"], Tone> = { Pending: "pending", Confirmed: "ledger", Rejected: "stamp", Cancelled: "neutral" };
const MAX_BYTES = 5 * 1024 * 1024;

export default function PortalPayPage() {
  const [version, setVersion] = useState(0);
  const [page, setPage] = useState(1);
  const summary = useQuery("portal:summary", () => api<PortalSummary>("/portal/summary"));
  const info = useQuery("portal:payment-info", () => api<PortalPaymentInfo>("/portal/payment-info"));
  const claims = useQuery(`portal:claims:${page}:${version}`, () => api<Paged<TenantClaim>>(`/portal/payment-claims?page=${page}&pageSize=5`));
  const currency = summary.data?.currency ?? "MXN";
  const [reporting, setReporting] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function cancelClaim(id: string) {
    setError(null); setNotice(null);
    try { await api(`/portal/payment-claims/${id}/cancel`, { method: "POST" }); setNotice("Reporte cancelado."); setVersion((v) => v + 1); }
    catch (e) { setError(e instanceof Error ? e.message : "No se pudo cancelar."); }
  }

  return (
    <div className="flex flex-col gap-6">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Pagar</h1>
        <p className="mt-1 text-sm text-ink-soft">
          {summary.data ? <>Saldo por pagar: <strong className="font-semibold text-ink">{formatMoney(summary.data.totalDue, currency)}</strong>.</> : "Elige cómo quieres pagar."} Tu pago cuenta cuando la administración lo confirma.
        </p>
      </div>
      <SuccessMessage message={notice} />
      <ErrorMessage message={error} />

      <Leaf>
        <header className="flex items-start gap-3 px-5 pb-4 pt-5">
          <Banknote size={18} className="mt-0.5 text-accent" aria-hidden />
          <div><h2 className="text-[15px] font-semibold">Transferencia</h2><p className="mt-0.5 text-sm text-ink-soft">Transfiere a esta cuenta y después reporta tu pago con el comprobante.</p></div>
        </header>
        <div className="perforation" />
        {!info.data && !info.error && <div className="p-5"><Skeleton className="h-20" /></div>}
        {info.error && <div className="p-5"><ErrorMessage message={info.error.message} /></div>}
        {info.data && !info.data.account && <p className="px-5 py-4 text-sm text-ink-soft">La administración aún no ha dado de alta una cuenta para tu departamento. Pregunta cómo pagar y, cuando lo hagas, reporta tu pago aquí.</p>}
        {info.data?.account && (
          <dl className="grid gap-x-6 gap-y-3 px-5 py-4 text-sm sm:grid-cols-2">
            <div><dt className="text-xs text-ink-faint">Titular</dt><dd className="font-medium">{info.data.account.holderName}</dd></div>
            <div><dt className="text-xs text-ink-faint">Banco</dt><dd className="font-medium">{info.data.account.bankName}</dd></div>
            <div className="sm:col-span-2">
              <dt className="text-xs text-ink-faint">CLABE</dt>
              <dd className="flex flex-wrap items-center gap-3"><span className="font-mono text-base font-semibold tabular-nums tracking-wide">{info.data.account.clabeFormatted}</span><CopyButton text={info.data.account.clabe} label="Copiar CLABE" /></dd>
            </div>
          </dl>
        )}
        <div className="perforation" />
        <div className="px-5 py-4">
          {!reporting
            ? <Button variant="secondary" onClick={() => { setReporting(true); setNotice(null); }}><FileCheck2 size={15} aria-hidden /> Ya pagué: reportar mi pago</Button>
            : <ReportForm currency={currency} onCancel={() => setReporting(false)} onDone={() => { setReporting(false); setNotice("Reporte enviado. La administración lo revisará."); setPage(1); setVersion((v) => v + 1); }} />}
        </div>
      </Leaf>

      <CashSection />

      <Leaf className="overflow-hidden">
        <header className="px-5 py-4"><h2 className="text-[15px] font-semibold">Mis reportes de pago</h2></header>
        <div className="perforation" />
        {!claims.data && !claims.error && <div className="flex flex-col gap-3 p-5">{[0, 1].map((i) => <Skeleton key={i} className="h-14" />)}</div>}
        {claims.error && <div className="p-5"><ErrorMessage message={claims.error.message} /></div>}
        {claims.data?.items.length === 0 && <EmptyState icon={<FileCheck2 size={18} />} title="Aún no has reportado pagos">Cuando transfieras, sube aquí tu comprobante.</EmptyState>}
        {claims.data && claims.data.items.length > 0 && (
          <ul>
            {claims.data.items.map((c) => (
              <li key={c.id} className="border-b border-rule px-5 py-4 last:border-b-0">
                <div className="flex items-start justify-between gap-4">
                  <div>
                    <p className="font-mono text-sm font-semibold tabular-nums">{formatMoney(c.amount, currency)}</p>
                    <p className="mt-0.5 text-xs text-ink-faint">{formatDate(c.paidOn)} · {paymentMethodLabels[c.method]}{c.reference ? ` · ${c.reference}` : ""}</p>
                    <p className="mt-0.5 text-xs text-ink-faint">Enviado {formatDateTime(c.createdAt)}</p>
                  </div>
                  <Badge tone={claimTone[c.status]}>{claimStatusLabels[c.status]}</Badge>
                </div>
                {c.status === "Rejected" && c.reviewComment && <p className="mt-2 rounded-md bg-stamp-soft px-3 py-2 text-sm text-stamp">{c.reviewComment}</p>}
                {c.status === "Pending" && <button className="mt-2 text-xs text-ink-soft underline hover:text-stamp" onClick={() => cancelClaim(c.id)}>Cancelar este reporte</button>}
              </li>
            ))}
          </ul>
        )}
        {claims.data && <Pager page={page} pageSize={5} total={claims.data.total} onPage={setPage} />}
      </Leaf>
    </div>
  );
}

function CopyButton({ text, label }: { text: string; label: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <Button type="button" variant="secondary" size="sm" onClick={async () => { await navigator.clipboard.writeText(text).catch(() => {}); setCopied(true); setTimeout(() => setCopied(false), 2000); }}>
      {copied ? <Check size={14} aria-hidden /> : <Copy size={14} aria-hidden />} {copied ? "Copiada" : label}
    </Button>
  );
}

/** Uploads the proof first, then reports. The uploaded file is remembered, so a retry after a failed report does not upload it again. */
function ReportForm({ currency, onCancel, onDone }: { currency: string; onCancel: () => void; onDone: () => void }) {
  const [file, setFile] = useState<File | null>(null);
  const uploaded = useRef<{ file: File; id: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    const amount = Number(f.get("amount"));
    if (!file) { setError("Adjunta el comprobante de tu pago."); return; }
    if (file.size > MAX_BYTES) { setError("El archivo pesa más de 5 MB."); return; }
    if (!(amount > 0)) { setError("Escribe el importe que pagaste."); return; }
    setBusy(true); setError(null);
    try {
      if (uploaded.current?.file !== file) {
        const body = new FormData(); body.append("file", file);
        const res = await fetch("/api/v1/files", { method: "POST", body, credentials: "same-origin" });
        if (!res.ok) { const p = await res.json().catch(() => null); throw new ApiError(res.status, p?.detail ?? p?.title ?? "No se pudo subir el comprobante."); }
        uploaded.current = { file, id: (await res.json()).id };
      }
      await api("/portal/payment-claims", { body: {
        amount, paidOn: f.get("paidOn"), method: f.get("method"), reference: f.get("reference") || null, note: f.get("note") || null, fileId: uploaded.current!.id,
      } });
      onDone();
    } catch (err) { setError(err instanceof Error ? err.message : "No se pudo enviar el reporte."); setBusy(false); }
  }

  return (
    <form onSubmit={submit} noValidate className="flex flex-col gap-4">
      <div className="grid gap-4 sm:grid-cols-3">
        <Field label={`Importe (${currency})`} name="amount" type="number" min={0.01} step="0.01" inputMode="decimal" required />
        <Field label="Fecha en que pagaste" name="paidOn" type="date" defaultValue={todayIso()} max={todayIso()} required />
        <Select label="Método" name="method" defaultValue="BankTransfer">{reportableMethods.map((m: PaymentMethod) => <option key={m} value={m}>{paymentMethodLabels[m]}</option>)}</Select>
      </div>
      <Field label="Clave de rastreo o referencia" name="reference" maxLength={100} hint="Opcional." />
      <Textarea label="Nota para la administración" name="note" maxLength={500} hint="Opcional. Por ejemplo, a qué mes corresponde." />
      <div className="flex flex-col gap-1.5">
        <label htmlFor="proof" className="text-sm font-medium">Comprobante</label>
        <input id="proof" type="file" accept="image/png,image/jpeg,image/webp,application/pdf" onChange={(e) => { setFile(e.target.files?.[0] ?? null); setError(null); }}
          className="block w-full text-sm file:mr-3 file:h-10 file:rounded-md file:border file:border-rule-strong file:bg-leaf file:px-4 file:text-sm file:font-medium hover:file:bg-paper" />
        <p className="text-xs text-ink-faint">Imagen (JPG, PNG, WebP) o PDF, de hasta 5 MB.</p>
      </div>
      <ErrorMessage message={error} />
      <div className="flex gap-2">
        <Button type="submit" loading={busy}><Upload size={15} aria-hidden /> Enviar reporte</Button>
        <Button type="button" variant="ghost" onClick={onCancel} disabled={busy}>Cancelar</Button>
      </div>
    </form>
  );
}

/** The cash QR is shown once, kept only in memory, and replaced or cancelled on request. Nothing is stored on the device. */
function CashSection() {
  const [code, setCode] = useState<CashCode | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!code) return;
    const t = setInterval(() => setNow(Date.now()), 15_000);
    return () => clearInterval(t);
  }, [code]);
  const expired = code ? new Date(code.expiresAt).getTime() <= now : false;

  async function generate() {
    setBusy(true); setError(null);
    try { setCode(await api<CashCode>("/portal/cash-token", { method: "POST" })); setNow(Date.now()); }
    catch (e) { setError(e instanceof Error ? e.message : "No se pudo generar el código."); }
    finally { setBusy(false); }
  }
  async function cancel() {
    setBusy(true); setError(null);
    try { await api("/portal/cash-token", { method: "DELETE" }); setCode(null); }
    catch (e) { setError(e instanceof Error ? e.message : "No se pudo cancelar."); }
    finally { setBusy(false); }
  }

  return (
    <Leaf>
      <header className="flex items-start gap-3 px-5 pb-4 pt-5">
        <QrCode size={18} className="mt-0.5 text-accent" aria-hidden />
        <div><h2 className="text-[15px] font-semibold">Efectivo</h2><p className="mt-0.5 text-sm text-ink-soft">Genera un código y muéstraselo a quien cobra. Sirve una sola vez y vence en 4 horas.</p></div>
      </header>
      <div className="perforation" />
      <div className="flex flex-col gap-4 px-5 py-4">
        {code && !expired && (
          <div className="flex flex-col items-center gap-3 rounded-md border border-rule bg-paper px-4 py-5 text-center">
            <QrImage value={code.qrPayload} label={`Código QR de pago en efectivo ${code.code}`} />
            <p className="font-mono text-xl font-semibold tracking-widest">{code.code}</p>
            <p className="text-xs text-ink-faint">Vence {formatDateTime(code.expiresAt)} · si falla la cámara, díctalo.</p>
          </div>
        )}
        {code && expired && <p className="rounded-md bg-paper-deep px-3 py-2 text-sm text-ink-soft">Este código venció. Genera uno nuevo.</p>}
        <ErrorMessage message={error} />
        <div className="flex flex-wrap gap-2">
          <Button variant={code && !expired ? "secondary" : "primary"} onClick={generate} loading={busy}>{code && !expired ? "Generar otro (cancela este)" : "Generar código de pago"}</Button>
          {code && !expired && <Button variant="ghost" onClick={cancel} disabled={busy}>Cancelar código</Button>}
        </div>
      </div>
    </Leaf>
  );
}
