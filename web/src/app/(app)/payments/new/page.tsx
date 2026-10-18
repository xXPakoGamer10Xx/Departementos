"use client";
import { ArrowLeft, Lock } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useRef, useState } from "react";
import { ChargeAllocator } from "@/components/charge-allocator";
import { useMe } from "@/components/me-context";
import { Button, EmptyState, EntityPicker, ErrorMessage, Field, Leaf, Select } from "@/components/ui";
import { ApiError, api, canWritePayments, formatMoney, fromCents, paymentMethodLabels, toCents, todayIso, type Paged, type Payment, type PaymentMethod, type Tenant } from "@/lib/api";

const searchTenants = (term: string) => api<Paged<Tenant>>(`/tenants?status=Active&pageSize=8&search=${encodeURIComponent(term)}`).then((r) => r.items);
const methods = Object.keys(paymentMethodLabels) as PaymentMethod[];

export default function NewPaymentPage() {
  const me = useMe();
  const router = useRouter();
  const currency = me.organizations.find((o) => o.id === me.activeOrganizationId)?.currency ?? "MXN";
  const [tenant, setTenant] = useState<Tenant | null>(null);
  const [amount, setAmount] = useState("");
  const [applied, setApplied] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  // One key per submission attempt: a retry after a network failure reuses it, so the server never creates the payment twice.
  const idempotencyKey = useRef(crypto.randomUUID());

  if (!canWritePayments(me.activeRole)) {
    return <Leaf><EmptyState icon={<Lock size={18} />} title="No tienes permiso para registrar pagos">Solo propietarios, administradores y finanzas pueden hacerlo.</EmptyState></Leaf>;
  }

  const amountCents = toCents(amount);
  const appliedCents = Object.values(applied).reduce((sum, v) => sum + toCents(v), 0);
  const unassignedCents = amountCents - appliedCents;
  const tooMuch = unassignedCents < 0;

  async function submit(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    if (!tenant) { setError("Elige un inquilino."); return; }
    if (tooMuch) { setError("Lo aplicado supera el importe del pago."); return; }
    const f = new FormData(e.currentTarget);
    const allocations = Object.entries(applied).filter(([, v]) => toCents(v) > 0).map(([chargeId, v]) => ({ chargeId, amount: fromCents(toCents(v)) }));
    setSaving(true); setError(null);
    try {
      const p = await api<Payment>("/payments", {
        headers: { "Idempotency-Key": idempotencyKey.current },
        body: { tenantId: tenant.id, receivedOn: f.get("receivedOn"), amount: fromCents(amountCents), method: f.get("method"), reference: f.get("reference") || null, notes: f.get("notes") || null, allocations },
      });
      idempotencyKey.current = crypto.randomUUID();
      router.push(`/payments/${p.id}`);
    } catch (err) {
      // A definitive answer from the server (any HTTP error) ends this attempt; no answer at all keeps the key for a safe retry.
      if (err instanceof ApiError) idempotencyKey.current = crypto.randomUUID();
      setError(err instanceof Error ? err.message : "No se pudo registrar el pago.");
      setSaving(false);
    }
  }

  return (
    <div className="flex flex-col gap-6">
      <div>
        <Link href="/payments" className="mb-3 inline-flex items-center gap-1.5 text-sm text-ink-soft hover:text-ink"><ArrowLeft size={14} aria-hidden /> Pagos</Link>
        <h1 className="text-2xl font-semibold tracking-tight">Registrar pago</h1>
        <p className="mt-1 text-sm text-ink-soft">Un pago confirmado no se edita ni se borra: si hay un error, se revierte con motivo y se registra otro.</p>
      </div>

      <form onSubmit={submit} noValidate className="flex flex-col gap-6">
        <Leaf>
          <header className="px-5 pb-4 pt-5"><h2 className="text-[15px] font-semibold">Datos del pago</h2></header>
          <div className="perforation" />
          <div className="flex flex-col gap-4 px-5 pb-5 pt-5">
            <EntityPicker label="Inquilino" placeholder="Buscar por nombre, correo o teléfono" selected={tenant} onSelect={(t) => { setTenant(t); setApplied({}); }} search={searchTenants} itemKey={(t) => t.id}
              renderItem={(t) => <span>{t.fullName}{t.email && <span className="text-ink-faint"> · {t.email}</span>}</span>} />
            <div className="grid gap-4 sm:grid-cols-3">
              <Field label={`Importe (${currency})`} type="number" min={0.01} step="0.01" inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} required />
              <Field label="Fecha de recepción" name="receivedOn" type="date" defaultValue={todayIso()} max={todayIso()} required />
              <Select label="Método" name="method" defaultValue="BankTransfer">{methods.map((m) => <option key={m} value={m}>{paymentMethodLabels[m]}</option>)}</Select>
            </div>
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label="Referencia" name="reference" maxLength={100} hint="Opcional. Folio, número de transferencia, etc." />
              <Field label="Notas" name="notes" maxLength={2000} hint="Opcional." />
            </div>
          </div>
        </Leaf>

        {tenant && (
          <Leaf>
            <header className="px-5 pb-4 pt-5"><h2 className="text-[15px] font-semibold">Aplicar a cargos</h2></header>
            <div className="perforation" />
            <div className="px-5 pb-5 pt-4">
              <ChargeAllocator tenantId={tenant.id} currency={currency} values={applied} onChange={setApplied} available={fromCents(Math.max(amountCents, 0))} />
            </div>
          </Leaf>
        )}

        <Leaf>
          <div className="flex flex-wrap items-center justify-between gap-4 px-5 py-4">
            <dl className="flex flex-wrap gap-x-8 gap-y-2 text-sm">
              <div><dt className="text-ink-soft">Importe del pago</dt><dd className="font-semibold">{formatMoney(fromCents(amountCents), currency)}</dd></div>
              <div><dt className="text-ink-soft">Aplicado a cargos</dt><dd className="font-semibold">{formatMoney(fromCents(appliedCents), currency)}</dd></div>
              <div><dt className="text-ink-soft">Saldo a favor</dt><dd className={`font-semibold ${tooMuch ? "text-stamp" : ""}`}>{formatMoney(fromCents(Math.max(unassignedCents, 0)), currency)}</dd></div>
            </dl>
            <Button type="submit" loading={saving} disabled={tooMuch || amountCents <= 0}>Confirmar pago</Button>
          </div>
          {tooMuch && <p role="alert" className="border-t border-rule px-5 py-3 text-sm text-stamp">Lo aplicado ({formatMoney(fromCents(appliedCents), currency)}) supera el importe del pago.</p>}
        </Leaf>
        <ErrorMessage message={error} />
      </form>
    </div>
  );
}
