"use client";
import { ArrowLeft, Lock } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { useMe } from "@/components/me-context";
import { Button, EmptyState, EntityPicker, ErrorMessage, Field, Leaf, Select, Textarea } from "@/components/ui";
import { api, canManage, issuePriorityLabels, roleLabels, type Issue, type IssuePriority, type Member, type Paged, type Property, type Unit } from "@/lib/api";
import { useQuery } from "@/lib/use-query";

const searchProperties = (term: string) => api<Paged<Property>>(`/properties?status=Active&pageSize=8&search=${encodeURIComponent(term)}`).then((r) => r.items);
const priorities = Object.keys(issuePriorityLabels) as IssuePriority[];

export default function NewIssuePage() {
  const me = useMe();
  const router = useRouter();
  const [property, setProperty] = useState<Property | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const units = useQuery(`units:${property?.id ?? "none"}`, () => (property ? api<Paged<Unit>>(`/properties/${property.id}/units?pageSize=100`) : Promise.resolve(null)));
  const members = useQuery("assignees", () => api<Member[]>("/members"));

  if (!canManage(me.activeRole)) {
    return <Leaf><EmptyState icon={<Lock size={18} />} title="No tienes permiso para crear incidencias">Solo propietarios y administradores pueden registrarlas y asignarlas.</EmptyState></Leaf>;
  }
  const assignees = (members.data ?? []).filter((m) => m.status === "Active" && (m.role === "Maintenance" || m.role === "Admin" || m.role === "Owner"));

  async function submit(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    if (!property) { setError("Elige una propiedad."); return; }
    const f = new FormData(e.currentTarget);
    setSaving(true); setError(null);
    try {
      const i = await api<Issue>("/maintenance-issues", { body: {
        propertyId: property.id, unitId: f.get("unitId") || null, title: f.get("title"), description: f.get("description") || null,
        priority: f.get("priority"), assignedToUserId: f.get("assignee") || null } });
      router.push(`/maintenance/${i.id}`);
    } catch (err) { setError(err instanceof Error ? err.message : "No se pudo crear la incidencia."); setSaving(false); }
  }

  return (
    <div className="flex flex-col gap-6">
      <div>
        <Link href="/maintenance" className="mb-3 inline-flex items-center gap-1.5 text-sm text-ink-soft hover:text-ink"><ArrowLeft size={14} aria-hidden /> Mantenimiento</Link>
        <h1 className="text-2xl font-semibold tracking-tight">Nueva incidencia</h1>
        <p className="mt-1 text-sm text-ink-soft">Describe el problema y dónde está. Puedes asignarla ahora o después.</p>
      </div>
      <Leaf>
        <header className="px-5 pb-4 pt-5"><h2 className="text-[15px] font-semibold">Detalles</h2></header>
        <div className="perforation" />
        <form onSubmit={submit} noValidate className="flex flex-col gap-4 px-5 pb-5 pt-5">
          <div className="grid gap-4 sm:grid-cols-2">
            <EntityPicker label="Propiedad" placeholder="Buscar propiedad" selected={property} onSelect={setProperty} search={searchProperties} itemKey={(p) => p.id}
              renderItem={(p) => <span>{p.name} <span className="text-ink-faint">· {p.address}</span></span>} />
            <Select label="Unidad" name="unitId" disabled={!property} defaultValue="" key={property?.id ?? "none"}>
              <option value="">{property ? "Toda la propiedad (áreas comunes)" : "Elige primero una propiedad"}</option>
              {units.data?.items.filter((u) => u.status !== "Archived").map((u) => <option key={u.id} value={u.id}>{u.code}</option>)}
            </Select>
          </div>
          <Field label="Título" name="title" maxLength={200} placeholder="Fuga en lavabo" required />
          <Textarea label="Descripción" name="description" maxLength={4000} hint="Qué pasa, desde cuándo y cómo contactar para acceder." />
          <div className="grid gap-4 sm:grid-cols-2">
            <Select label="Prioridad" name="priority" defaultValue="Normal">{priorities.map((p) => <option key={p} value={p}>{issuePriorityLabels[p]}</option>)}</Select>
            <Select label="Asignar a" name="assignee" defaultValue="">
              <option value="">Sin asignar</option>
              {assignees.map((m) => <option key={m.userId} value={m.userId}>{m.fullName ?? m.email} · {roleLabels[m.role]}</option>)}
            </Select>
          </div>
          <ErrorMessage message={error} />
          <div className="flex justify-end gap-2">
            <Link href="/maintenance" className="inline-flex h-10 items-center rounded-md px-4 text-sm font-medium text-ink-soft hover:bg-paper-deep hover:text-ink">Cancelar</Link>
            <Button type="submit" loading={saving}>Crear incidencia</Button>
          </div>
        </form>
      </Leaf>
    </div>
  );
}
