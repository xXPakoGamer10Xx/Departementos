"use client";
import { ArrowLeft } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { useMe } from "@/components/me-context";
import { Button, EmptyState, EntityPicker, ErrorMessage, Field, Leaf, Textarea } from "@/components/ui";
import { api, canManage, formatMoney, type Lease, type Paged, type Tenant, type UnitSearch, unitTypeLabels } from "@/lib/api";

const searchUnits = (term: string) => api<Paged<UnitSearch>>(`/units?pageSize=8&search=${encodeURIComponent(term)}`).then((r) => r.items);
const searchTenants = (term: string) => api<Paged<Tenant>>(`/tenants?status=Active&pageSize=8&search=${encodeURIComponent(term)}`).then((r) => r.items);

export default function NewLeasePage() {
  const me = useMe();
  const router = useRouter();
  const currency = me.organizations.find((o) => o.id === me.activeOrganizationId)?.currency ?? "MXN";
  const [unit, setUnit] = useState<UnitSearch | null>(null);
  const [tenant, setTenant] = useState<Tenant | null>(null);
  const [rent, setRent] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  if (!canManage(me.activeRole)) {
    return <Leaf><EmptyState icon={<ArrowLeft size={18} />} title="No tienes permiso para crear contratos">Solo los propietarios y administradores pueden hacerlo.</EmptyState></Leaf>;
  }

  async function submit(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    if (!unit || !tenant) { setError("Elige una unidad y un inquilino."); return; }
    const f = new FormData(e.currentTarget);
    setSaving(true); setError(null);
    try {
      const l = await api<Lease>("/leases", { body: {
        unitId: unit.id, tenantId: tenant.id, startDate: f.get("startDate"), endDate: f.get("endDate") || null,
        monthlyRent: Number(rent), dueDay: Number(f.get("dueDay")), depositAmount: f.get("deposit") === "" ? null : Number(f.get("deposit")), termsNotes: f.get("terms") || null } });
      router.push(`/leases/${l.id}`);
    } catch (err) { setError(err instanceof Error ? err.message : "No se pudo crear el contrato."); setSaving(false); }
  }

  return (
    <div className="flex flex-col gap-6">
      <div>
        <Link href="/leases" className="mb-3 inline-flex items-center gap-1.5 text-sm text-ink-soft hover:text-ink"><ArrowLeft size={14} aria-hidden /> Contratos</Link>
        <h1 className="text-2xl font-semibold tracking-tight">Nuevo contrato</h1>
        <p className="mt-1 text-sm text-ink-soft">Se guarda como borrador. Podrás revisarlo y activarlo después; los cargos se generan aparte.</p>
      </div>

      <Leaf>
        <header className="px-5 pb-4 pt-5"><h2 className="text-[15px] font-semibold">Unidad e inquilino</h2></header>
        <div className="perforation" />
        <form onSubmit={submit} noValidate className="flex flex-col gap-5 px-5 pb-5 pt-5">
          <div className="grid gap-4 sm:grid-cols-2">
            <EntityPicker label="Unidad" placeholder="Buscar por código o propiedad" selected={unit}
              onSelect={(u) => { setUnit(u); if (u?.baseRent != null) setRent(String(u.baseRent)); }} search={searchUnits} itemKey={(u) => u.id}
              renderItem={(u) => <span><span className="font-mono font-medium">{u.code}</span> · {u.propertyName} <span className="text-ink-faint">· {unitTypeLabels[u.type]}</span></span>} />
            <EntityPicker label="Inquilino" placeholder="Buscar por nombre, correo o teléfono" selected={tenant} onSelect={setTenant} search={searchTenants} itemKey={(t) => t.id}
              renderItem={(t) => <span>{t.fullName}{t.email && <span className="text-ink-faint"> · {t.email}</span>}</span>} />
          </div>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Inicio" name="startDate" type="date" required />
            <Field label="Fin" name="endDate" type="date" hint="Opcional. Sin fecha final, el contrato queda abierto." />
          </div>
          <div className="grid gap-4 sm:grid-cols-3">
            <Field label={`Renta mensual (${currency})`} name="rent" type="number" min={0} step="0.01" value={rent} onChange={(e) => setRent(e.target.value)}
              hint={unit?.baseRent != null ? `Renta base de la unidad: ${formatMoney(unit.baseRent, currency)}` : undefined} required />
            <Field label="Día de vencimiento" name="dueDay" type="number" min={1} max={28} defaultValue={1} hint="Del 1 al 28." required />
            <Field label={`Depósito (${currency})`} name="deposit" type="number" min={0} step="0.01" hint="Opcional. Solo dato del contrato." />
          </div>
          <Textarea label="Condiciones y notas" name="terms" maxLength={4000} hint="Opcional." />
          <ErrorMessage message={error} />
          <div className="flex justify-end gap-2">
            <Link href="/leases" className="inline-flex h-10 items-center rounded-md px-4 text-sm font-medium text-ink-soft hover:bg-paper-deep hover:text-ink">Cancelar</Link>
            <Button type="submit" loading={saving}>Guardar borrador</Button>
          </div>
        </form>
      </Leaf>
    </div>
  );
}
