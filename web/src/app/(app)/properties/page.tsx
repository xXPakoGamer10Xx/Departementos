"use client";
import { Building2, ChevronRight, Plus } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { useMe } from "@/components/me-context";
import { Badge, Button, EmptyState, ErrorMessage, Field, Leaf, Pager, SearchInput, Select, Skeleton, Textarea } from "@/components/ui";
import { api, canManage, type Paged, type Property, type PropertyStatus } from "@/lib/api";
import { useQuery } from "@/lib/use-query";

const PAGE_SIZE = 25;

export default function PropertiesPage() {
  const me = useMe();
  const router = useRouter();
  const admin = canManage(me.activeRole);
  const [search, setSearch] = useState("");
  const [debounced, setDebounced] = useState("");
  const [status, setStatus] = useState<PropertyStatus>("Active");
  const [page, setPage] = useState(1);
  const [creating, setCreating] = useState(false);
  const [saving, setSaving] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);

  useEffect(() => {
    const t = setTimeout(() => { setDebounced(search); setPage(1); }, 250);
    return () => clearTimeout(t);
  }, [search]);

  const params = new URLSearchParams({ status, page: String(page), pageSize: String(PAGE_SIZE) });
  if (debounced) params.set("search", debounced);
  const qs = params.toString();
  const { data, error, loading } = useQuery(qs, () => api<Paged<Property>>(`/properties?${qs}`));

  async function create(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    setSaving(true); setFormError(null);
    try {
      const p = await api<Property>("/properties", { body: { name: f.get("name"), address: f.get("address"), notes: f.get("notes") || null } });
      router.push(`/properties/${p.id}`);
    } catch (err) { setFormError(err instanceof Error ? err.message : "No se pudo crear la propiedad."); setSaving(false); }
  }

  return (
    <div className="flex flex-col gap-6">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Propiedades</h1>
          <p className="mt-1 text-sm text-ink-soft">Edificios, casas y conjuntos con sus unidades rentables.</p>
        </div>
        {admin && !creating && <Button onClick={() => setCreating(true)}><Plus size={16} aria-hidden /> Nueva propiedad</Button>}
      </div>

      {creating && (
        <Leaf>
          <header className="px-5 pb-4 pt-5"><h2 className="text-[15px] font-semibold">Nueva propiedad</h2></header>
          <div className="perforation" />
          <form onSubmit={create} className="flex flex-col gap-4 px-5 pb-5 pt-4">
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label="Nombre" name="name" placeholder="Torre Norte" maxLength={200} required />
              <Field label="Dirección" name="address" placeholder="Calle, número, colonia, ciudad" maxLength={500} required />
            </div>
            <Textarea label="Notas" name="notes" maxLength={2000} hint="Opcional. Solo visible para tu equipo." />
            <ErrorMessage message={formError} />
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" onClick={() => { setCreating(false); setFormError(null); }}>Cancelar</Button>
              <Button type="submit" loading={saving}>Crear propiedad</Button>
            </div>
          </form>
        </Leaf>
      )}

      <Leaf className="overflow-hidden">
        <div className="grid gap-3 border-b border-rule p-4 sm:grid-cols-[minmax(0,1fr)_180px]">
          <SearchInput label="Buscar propiedades" placeholder="Buscar por nombre o dirección" value={search} onChange={(e) => setSearch(e.target.value)} />
          <Select aria-label="Estado" value={status} onChange={(e) => { setStatus(e.target.value as PropertyStatus); setPage(1); }}>
            <option value="Active">Activas</option>
            <option value="Archived">Archivadas</option>
          </Select>
        </div>

        {error && <div className="p-5"><ErrorMessage message={error.message} /></div>}
        {!data && !error && <div className="flex flex-col gap-3 p-5">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-12" />)}</div>}
        {data && data.items.length === 0 && (
          <EmptyState icon={<Building2 size={18} />} title={debounced ? "Sin resultados" : status === "Active" ? "Aún no hay propiedades" : "No hay propiedades archivadas"}>
            {debounced ? "Prueba con otro nombre o dirección." : status === "Active" ? (admin ? "Crea tu primera propiedad para registrar sus unidades." : "Un administrador aún no ha registrado propiedades.") : undefined}
          </EmptyState>
        )}
        {data && data.items.length > 0 && (
          <ul className={loading ? "opacity-60 transition-opacity" : "transition-opacity"}>
            {data.items.map((p) => (
              <li key={p.id} className="border-b border-rule last:border-b-0">
                <Link href={`/properties/${p.id}`} className="group flex items-center justify-between gap-4 px-5 py-4 transition-colors hover:bg-accent-soft/40">
                  <div className="min-w-0">
                    <p className="truncate font-medium">{p.name}</p>
                    <p className="truncate text-sm text-ink-soft">{p.address}</p>
                  </div>
                  <div className="flex shrink-0 items-center gap-3">
                    {p.status === "Archived" && <Badge>Archivada</Badge>}
                    <span className="text-sm text-ink-soft">{p.unitCount} {p.unitCount === 1 ? "unidad" : "unidades"}</span>
                    <ChevronRight size={16} className="text-ink-faint transition-transform group-hover:translate-x-0.5" aria-hidden />
                  </div>
                </Link>
              </li>
            ))}
          </ul>
        )}
        {data && <Pager page={data.page} pageSize={data.pageSize} total={data.total} onPage={setPage} />}
      </Leaf>
    </div>
  );
}
