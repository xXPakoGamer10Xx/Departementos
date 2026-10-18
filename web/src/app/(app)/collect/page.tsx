"use client";
import { CheckCircle2, Keyboard, Lock, ScanLine } from "lucide-react";
import Link from "next/link";
import { useState } from "react";
import { ChargeAllocator } from "@/components/charge-allocator";
import { useMe } from "@/components/me-context";
import { QrScanner } from "@/components/qr";
import { Badge, Button, EmptyState, ErrorMessage, Field, Leaf } from "@/components/ui";
import { ApiError, api, canWritePayments, chargeStatusLabels, formatDateTime, formatMoney, fromCents, toCents, type CashTokenInfo, type Payment } from "@/lib/api";
import { chargeTone } from "@/lib/charge-ui";

type Step = { name: "start" } | { name: "ready"; info: CashTokenInfo } | { name: "done"; payment: Payment; tenantName: string; currency: string };

export default function CollectPage() {
  const me = useMe();
  const [step, setStep] = useState<Step>({ name: "start" });
  if (!canWritePayments(me.activeRole)) {
    return <Leaf><EmptyState icon={<Lock size={18} />} title="No tienes permiso para cobrar">Solo propietarios, administradores, finanzas y cobradores pueden registrar cobros.</EmptyState></Leaf>;
  }
  return (
    <div className="flex flex-col gap-6">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Cobrar en efectivo</h1>
        <p className="mt-1 text-sm text-ink-soft">Escanea el código QR del inquilino, cuenta el dinero y registra el cobro.</p>
      </div>
      {step.name === "start" && <StartStep onResolved={(info) => setStep({ name: "ready", info })} />}
      {step.name === "ready" && <ReadyStep info={step.info} onBack={() => setStep({ name: "start" })} onDone={(payment) => setStep({ name: "done", payment, tenantName: step.info.tenantName, currency: step.info.currency })} />}
      {step.name === "done" && (
        <Leaf>
          <div className="flex flex-col items-center gap-3 px-6 py-10 text-center">
            <CheckCircle2 size={36} className="text-ledger" aria-hidden />
            <h2 className="text-lg font-semibold">Cobro registrado</h2>
            <p className="font-mono text-3xl font-semibold tabular-nums">{formatMoney(step.payment.amount, step.currency)}</p>
            <p className="text-sm text-ink-soft">de {step.tenantName}</p>
            <div className="mt-2 flex flex-wrap justify-center gap-2">
              <Button onClick={() => setStep({ name: "start" })}>Cobrar a otra persona</Button>
              {me.activeRole !== "Collector" && <Link href={`/payments/${step.payment.id}`} className="inline-flex h-10 items-center rounded-md border border-rule-strong bg-leaf px-4 text-sm font-medium hover:bg-paper">Ver el pago</Link>}
            </div>
          </div>
        </Leaf>
      )}
    </div>
  );
}

function StartStep({ onResolved }: { onResolved: (info: CashTokenInfo) => void }) {
  const [mode, setMode] = useState<"scan" | "type">("scan");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function resolve(code: string) {
    if (busy) return;
    setBusy(true); setError(null);
    try { onResolved(await api<CashTokenInfo>("/cash-tokens/resolve", { body: { code } })); }
    catch (e) { setError(e instanceof ApiError && e.status === 429 ? "Demasiados intentos. Espera un momento." : e instanceof Error ? e.message : "No se pudo leer el código."); setBusy(false); }
  }

  return (
    <Leaf>
      <div className="flex gap-2 px-5 pt-5">
        <Button size="sm" variant={mode === "scan" ? "primary" : "secondary"} onClick={() => { setMode("scan"); setError(null); }}><ScanLine size={14} aria-hidden /> Escanear QR</Button>
        <Button size="sm" variant={mode === "type" ? "primary" : "secondary"} onClick={() => { setMode("type"); setError(null); }}><Keyboard size={14} aria-hidden /> Escribir código</Button>
      </div>
      <div className="flex flex-col gap-4 px-5 pb-5 pt-4">
        {mode === "scan" && (
          <div className="mx-auto w-full max-w-sm">
            <QrScanner onCode={resolve} onProblem={(m) => { setError(m); setMode("type"); }} />
            <p className="mt-2 text-center text-xs text-ink-faint">Apunta la cámara al código QR del inquilino.</p>
          </div>
        )}
        {mode === "type" && (
          <form noValidate className="flex max-w-sm flex-col gap-3" onSubmit={(e) => { e.preventDefault(); resolve(String(new FormData(e.currentTarget).get("code") ?? "")); }}>
            <Field label="Código del inquilino" name="code" placeholder="ABCD-EFGH-JK" autoComplete="off" autoCapitalize="characters" spellCheck={false} className="font-mono uppercase tracking-widest" required />
            <div><Button type="submit" loading={busy}>Buscar</Button></div>
          </form>
        )}
        <ErrorMessage message={error} />
      </div>
    </Leaf>
  );
}

function ReadyStep({ info, onBack, onDone }: { info: CashTokenInfo; onBack: () => void; onDone: (payment: Payment) => void }) {
  const total = info.charges.reduce((sum, c) => sum + toCents(c.balance), 0);
  const [amount, setAmount] = useState(String(fromCents(total)));
  const [applied, setApplied] = useState<Record<string, string>>(() => Object.fromEntries(info.charges.map((c) => [c.chargeId, String(c.balance)])));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const amountCents = toCents(amount);
  const appliedCents = Object.values(applied).reduce((sum, v) => sum + toCents(v), 0);
  const tooMuch = appliedCents > amountCents;

  async function collect() {
    setBusy(true); setError(null);
    try {
      // The request is the same if repeated, so a tap after a dropped connection is safe: the server answers with the payment already made.
      const payment = await api<Payment>(`/cash-tokens/${info.tokenId}/collect`, { body: {
        amount: fromCents(amountCents),
        allocations: Object.entries(applied).filter(([, v]) => toCents(v) > 0).map(([chargeId, v]) => ({ chargeId, amount: fromCents(toCents(v)) })),
      } });
      onDone(payment);
    } catch (e) { setError(e instanceof Error ? e.message : "No se pudo registrar el cobro."); setBusy(false); }
  }

  return (
    <div className="flex flex-col gap-6">
      <Leaf>
        <div className="flex flex-wrap items-start justify-between gap-3 px-5 py-5">
          <div><p className="text-xs text-ink-faint">Inquilino</p><h2 className="text-xl font-semibold">{info.tenantName}</h2><p className="mt-0.5 text-xs text-ink-faint">Código válido hasta {formatDateTime(info.expiresAt)}</p></div>
          <div className="text-right"><p className="text-xs text-ink-faint">Debe en total</p><p className="font-mono text-xl font-semibold tabular-nums">{formatMoney(fromCents(total), info.currency)}</p></div>
        </div>
      </Leaf>
      {info.charges.length === 0 ? (
        <Leaf><EmptyState icon={<CheckCircle2 size={18} />} title="No tiene cargos pendientes">Si cobras de todos modos, el dinero quedará como saldo a favor.</EmptyState></Leaf>
      ) : (
        <Leaf>
          <header className="px-5 pb-3 pt-5"><h2 className="text-[15px] font-semibold">Cargos pendientes</h2></header>
          <ul className="px-5 pb-4 text-sm">
            {info.charges.map((c) => (
              <li key={c.chargeId} className="flex items-start justify-between gap-3 border-t border-rule py-2 first:border-t-0">
                <span>{c.description}<span className="block text-xs text-ink-faint">{c.unitCode} · vence {c.dueDate}</span></span>
                <span className="text-right"><span className="font-mono tabular-nums">{formatMoney(c.balance, info.currency)}</span><span className="mt-0.5 block"><Badge tone={chargeTone[c.status]}>{chargeStatusLabels[c.status]}</Badge></span></span>
              </li>
            ))}
          </ul>
        </Leaf>
      )}
      <Leaf>
        <div className="flex flex-col gap-4 px-5 py-5">
          <Field label={`Dinero recibido (${info.currency})`} type="number" min={0.01} step="0.01" inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} className="text-lg font-semibold" />
          {info.charges.length > 0 && <ChargeAllocator tenantId={info.tenantId} currency={info.currency} values={applied} onChange={setApplied} available={fromCents(Math.max(amountCents, 0))} />}
          <p className={`text-sm ${tooMuch ? "text-stamp" : "text-ink-soft"}`}>
            Aplicado {formatMoney(fromCents(appliedCents), info.currency)} · saldo a favor {formatMoney(fromCents(Math.max(amountCents - appliedCents, 0)), info.currency)}
            {tooMuch && " — lo aplicado supera lo recibido"}
          </p>
          <ErrorMessage message={error} />
          <div className="flex flex-wrap gap-2">
            <Button onClick={collect} loading={busy} disabled={amountCents <= 0 || tooMuch}>Cobrar {formatMoney(fromCents(Math.max(amountCents, 0)), info.currency)}</Button>
            <Button variant="ghost" onClick={onBack} disabled={busy}>Volver</Button>
          </div>
        </div>
      </Leaf>
    </div>
  );
}
