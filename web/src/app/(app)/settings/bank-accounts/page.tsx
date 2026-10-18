"use client";
import { ArrowLeft, Landmark, Lock, Plus } from "lucide-react";
import Link from "next/link";
import { useState } from "react";
import { useMe } from "@/components/me-context";
import { Badge, Button, EmptyState, ErrorMessage, Field, Leaf, Select, Skeleton, SuccessMessage } from "@/components/ui";
import { api, canManage, type BankAccount, type Paged, type Property } from "@/lib/api";
import { isValidClabe, normalizeClabe } from "@/lib/clabe";
import { useQuery } from "@/lib/use-query";

export default function BankAccountsPage() {
  const me = useMe();
  const allowed = canManage(me.activeRole);
  const [version, setVersion] = useState(0);
  const [creating, setCreating] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const accounts = useQuery(`bank-accounts:${version}`, () => (allowed ? api<BankAccount[]>("/bank-accounts") : Promise.reject({ message: "forbidden", status: 403 })));
  const properties = useQuery("bank-account-properties", () => (allowed ? api<Paged<Property>>("/properties?status=Active&pageSize=100") : Promise.resolve(null)));

  async function act(action: () => Promise<unknown>, success: string) {
    setBusy(true); setError(null); setNotice(null);
    try { await action(); setNotice(success); setVersion((v) => v + 1); return true; }
    catch (e) { setError(e instanceof Error ? e.message : "No se pudo completar la acción."); return false; }
    finally { setBusy(false); }
  }

  if (!allowed || accounts.error?.status === 403) {
    return <Leaf><EmptyState icon={<Lock size={18} />} title="No tienes permiso para ver esta sección">Solo los propietarios y administradores manejan las cuentas bancarias.</EmptyState></Leaf>;
  }

  return (
    <div className="flex flex-col gap-6">
      <div>
        <Link href="/settings" className="mb-3 inline-flex items-center gap-1.5 text-sm text-ink-soft hover:text-ink"><ArrowLeft size={14} aria-hidden /> Ajustes</Link>
        <div className="flex flex-wrap items-end justify-between gap-4">
          <div>
            <h1 className="text-2xl font-semibold tracking-tight">Cuentas bancarias</h1>
            <p className="mt-1 text-sm text-ink-soft">Donde los inquilinos transfieren. Una cuenta por propiedad, o una general para las demás.</p>
          </div>
          <Button onClick={() => { setCreating((v) => !v); setNotice(null); }}><Plus size={15} aria-hidden /> Nueva cuenta</Button>
        </div>
      </div>
      <SuccessMessage message={notice} />
      <ErrorMessage message={error ?? (accounts.error?.status === 403 ? null : accounts.error?.message) ?? null} />
      {creating && <NewAccount properties={properties.data?.items ?? []} hasGeneral={(accounts.data ?? []).some((a) => !a.propertyId)} busy={busy}
        onCancel={() => setCreating(false)} onSubmit={async (body) => { if (await act(() => api("/bank-accounts", { body }), "Cuenta dada de alta.")) setCreating(false); }} />}

      <Leaf className="overflow-hidden">
        {!accounts.data && !accounts.error && <div className="flex flex-col gap-3 p-5">{[0, 1].map((i) => <Skeleton key={i} className="h-16" />)}</div>}
        {accounts.data?.length === 0 && <EmptyState icon={<Landmark size={18} />} title="Aún no hay cuentas">Da de alta la cuenta donde cobras la renta para que tus inquilinos la vean al pagar.</EmptyState>}
        {accounts.data && accounts.data.length > 0 && (
          <ul>
            {accounts.data.map((a) => (
              <li key={a.id} className="flex flex-wrap items-center justify-between gap-4 border-b border-rule px-5 py-4 last:border-b-0">
                <div className="min-w-0">
                  <p className="text-sm font-semibold">{a.holderName}</p>
                  <p className="mt-0.5 text-sm text-ink-soft">{a.bankName} · <span className="font-mono tabular-nums">{a.clabeFormatted}</span></p>
                  <div className="mt-1.5 flex gap-1.5">
                    <Badge tone="neutral">{a.propertyName ? `Solo ${a.propertyName}` : "General"}</Badge>
                    {a.isDefault && <Badge tone="accent">Predeterminada</Badge>}
                    {a.status === "Archived" && <Badge tone="neutral">Archivada</Badge>}
                  </div>
                </div>
                {a.status === "Active" && (
                  <div className="flex gap-2">
                    {!a.propertyId && !a.isDefault && <Button size="sm" variant="secondary" disabled={busy} onClick={() => act(() => api(`/bank-accounts/${a.id}`, { method: "PATCH", body: { isDefault: true } }), "Cuenta predeterminada actualizada.")}>Hacer predeterminada</Button>}
                    <Button size="sm" variant="danger" disabled={busy} onClick={() => act(() => api(`/bank-accounts/${a.id}/archive`, { method: "POST" }), "Cuenta archivada.")}>Archivar</Button>
                  </div>
                )}
              </li>
            ))}
          </ul>
        )}
      </Leaf>
    </div>
  );
}

function NewAccount({ properties, hasGeneral, busy, onCancel, onSubmit }: {
  properties: Property[]; hasGeneral: boolean; busy: boolean; onCancel: () => void; onSubmit: (body: object) => void;
}) {
  const [clabe, setClabe] = useState("");
  const [propertyId, setPropertyId] = useState("");
  const [touched, setTouched] = useState(false);
  const normalized = normalizeClabe(clabe);
  const valid = isValidClabe(clabe);
  const clabeError = touched && normalized.length > 0 && !valid ? "La CLABE debe tener 18 dígitos y un dígito verificador correcto. Revisa que no falte ni sobre ninguno." : null;

  return (
    <Leaf>
      <header className="px-5 pb-4 pt-5"><h2 className="text-[15px] font-semibold">Nueva cuenta</h2></header>
      <div className="perforation" />
      <form noValidate className="flex flex-col gap-4 px-5 pb-5 pt-4" onSubmit={(e) => {
        e.preventDefault(); setTouched(true);
        const f = new FormData(e.currentTarget);
        if (!valid) return;
        onSubmit({ holderName: f.get("holderName"), clabe: normalized, propertyId: propertyId || null, isDefault: !propertyId && f.get("isDefault") === "on" });
      }}>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Titular" name="holderName" maxLength={200} required />
          <Field label="CLABE" value={clabe} onChange={(e) => setClabe(e.target.value)} onBlur={() => setTouched(true)} inputMode="numeric" autoComplete="off" error={clabeError} hint="18 dígitos. El banco se detecta solo." className="font-mono" required />
        </div>
        <Select label="¿Para qué propiedad?" value={propertyId} onChange={(e) => setPropertyId(e.target.value)}>
          <option value="">General (todas las que no tengan una propia)</option>
          {properties.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
        </Select>
        {!propertyId && hasGeneral && (
          <label className="flex items-center gap-2 text-sm"><input type="checkbox" name="isDefault" className="size-4 accent-[var(--color-accent)]" /> Hacerla la cuenta predeterminada</label>
        )}
        <div className="flex gap-2"><Button type="submit" loading={busy}>Guardar cuenta</Button><Button type="button" variant="ghost" onClick={onCancel} disabled={busy}>Cancelar</Button></div>
      </form>
    </Leaf>
  );
}
