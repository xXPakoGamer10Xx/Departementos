"use client";
import { ChevronRight, Plus, UserRound } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { useMe } from "@/components/me-context";
import { Avatar, Badge, Button, EmptyState, ErrorMessage, Field, Leaf, Pager, SearchInput, Select, Skeleton, Textarea } from "@/components/ui";
import { api, canManage, type Paged, type Tenant, type TenantStatus } from "@/lib/api";
import { useQuery } from "@/lib/use-query";

const PAGE_SIZE = 25;

export default function TenantsPage() {
  const me = useMe();
  const router = useRouter();
  const admin = canManage(me.activeRole);
  const [search, setSearch] = useState("");
  const [debounced, setDebounced] = useState("");
  const [status, setStatus] = useState<TenantStatus>("Active");
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
  const { data, error, loading } = useQuery(qs, () => api<Paged<Tenant>>(`/tenants?${qs}`));

  async function create(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    setSaving(true); setFormError(null);
    try {
      const t = await api<Tenant>("/tenants", { body: { fullName: f.get("fullName"), email: f.get("email") || null, phone: f.get("phone") || null, notes: f.get("notes") || null } });
      router.push(`/tenants/${t.id}`);
    } catch (err) { setFormError(err instanceof Error ? err.message : "No se pudo crear el inquilino."); setSaving(false); }
  }

  return (
    <div className="flex flex-col gap-6">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Inquilinos</h1>
          <p className="mt-1 text-sm text-ink-soft">Personas con contrato o que pueden tenerlo, y sus datos de contacto.</p>
        </div>
        {admin && !creating && <Button onClick={() => setCreating(true)}><Plus size={16} aria-hidden /> Nuevo inquilino</Button>}
      </div>

      {creating && (
        <Leaf>
          <header className="px-5 pb-4 pt-5"><h2 className="text-[15px] font-semibold">Nuevo inquilino</h2></header>
          <div className="perforation" />
          <form onSubmit={create} noValidate className="flex flex-col gap-4 px-5 pb-5 pt-4">
            <Field label="Nombre completo" name="fullName" autoComplete="off" maxLength={200} required />
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label="Correo electrónico" name="email" type="email" autoComplete="off" hint="Opcional." />
              <Field label="Teléfono" name="phone" type="tel" autoComplete="off" hint="Opcional. Al menos 7 dígitos." />
            </div>
            <Textarea label="Notas" name="notes" maxLength={2000} hint="Opcional. No guardes identificaciones oficiales ni datos sensibles." />
            <ErrorMessage message={formError} />
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" onClick={() => { setCreating(false); setFormError(null); }}>Cancelar</Button>
              <Button type="submit" loading={saving}>Crear inquilino</Button>
            </div>
          </form>
        </Leaf>
      )}

      <Leaf className="overflow-hidden">
        <div className="grid gap-3 border-b border-rule p-4 sm:grid-cols-[minmax(0,1fr)_180px]">
          <SearchInput label="Buscar inquilinos" placeholder="Buscar por nombre, correo o teléfono" value={search} onChange={(e) => setSearch(e.target.value)} />
          <Select aria-label="Estado" value={status} onChange={(e) => { setStatus(e.target.value as TenantStatus); setPage(1); }}>
            <option value="Active">Activos</option>
            <option value="Archived">Archivados</option>
          </Select>
        </div>

        {error && <div className="p-5"><ErrorMessage message={error.message} /></div>}
        {!data && !error && <div className="flex flex-col gap-3 p-5">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-12" />)}</div>}
        {data && data.items.length === 0 && (
          <EmptyState icon={<UserRound size={18} />} title={debounced ? "Sin resultados" : status === "Active" ? "Aún no hay inquilinos" : "No hay inquilinos archivados"}>
            {debounced ? "Prueba con otro nombre, correo o teléfono." : status === "Active" ? (admin ? "Registra a tu primer inquilino para poder crear su contrato." : "Un administrador aún no ha registrado inquilinos.") : undefined}
          </EmptyState>
        )}
        {data && data.items.length > 0 && (
          <ul className={loading ? "opacity-60 transition-opacity" : "transition-opacity"}>
            {data.items.map((t) => (
              <li key={t.id} className="border-b border-rule last:border-b-0">
                <Link href={`/tenants/${t.id}`} className="group flex items-center justify-between gap-4 px-5 py-3.5 transition-colors hover:bg-accent-soft/40">
                  <div className="flex min-w-0 items-center gap-3">
                    <Avatar name={t.fullName} />
                    <div className="min-w-0">
                      <p className="truncate font-medium">{t.fullName}</p>
                      <p className="truncate text-sm text-ink-soft">{[t.email, t.phone].filter(Boolean).join(" · ") || "Sin datos de contacto"}</p>
                    </div>
                  </div>
                  <div className="flex shrink-0 items-center gap-3">
                    {t.status === "Archived" && <Badge>Archivado</Badge>}
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
