"use client";
import { Archive, ArrowLeft, Building2, DoorOpen, Pencil, Plus, RotateCcw } from "lucide-react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useEffect, useState } from "react";
import { useMe } from "@/components/me-context";
import { Badge, Button, EmptyState, ErrorMessage, Field, Leaf, Pager, SearchInput, Select, Skeleton, SuccessMessage, Textarea, type Tone } from "@/components/ui";
import {
  api, canManage, formatMoney, unitStatusLabels, unitTypeLabels,
  type Paged, type Property, type Unit, type UnitStatus, type UnitType,
} from "@/lib/api";
import { useQuery } from "@/lib/use-query";

const PAGE_SIZE = 50;
const unitTypes = Object.keys(unitTypeLabels) as UnitType[];
const editableStatuses: UnitStatus[] = ["Available", "Maintenance", "Archived"];
const statusTone: Record<UnitStatus, Tone> = { Available: "ledger", Occupied: "neutral", Maintenance: "pending", Archived: "neutral" };

export default function PropertyDetailPage() {
  const { id } = useParams<{ id: string }>();
  const me = useMe();
  const admin = canManage(me.activeRole);
  const showRent = me.activeRole !== "Maintenance";
  const currency = me.organizations.find((o) => o.id === me.activeOrganizationId)?.currency ?? "MXN";

  const [version, setVersion] = useState(0);
  const refresh = () => setVersion((v) => v + 1);
  const [panel, setPanel] = useState<"edit" | "archive" | "addUnit" | null>(null);
  const [editingUnit, setEditingUnit] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const [search, setSearch] = useState("");
  const [debounced, setDebounced] = useState("");
  const [status, setStatus] = useState<"" | UnitStatus>("");
  const [page, setPage] = useState(1);
  useEffect(() => {
    const t = setTimeout(() => { setDebounced(search); setPage(1); }, 250);
    return () => clearTimeout(t);
  }, [search]);

  const property = useQuery(`p:${id}:${version}`, () => api<Property>(`/properties/${id}`));
  const up = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
  if (debounced) up.set("search", debounced);
  if (status) up.set("status", status);
  const units = useQuery(`u:${id}:${up}:${version}`, () => api<Paged<Unit>>(`/properties/${id}/units?${up}`));

  async function run(action: () => Promise<unknown>, success: string, after?: () => void) {
    setBusy(true); setError(null); setNotice(null);
    try { await action(); setNotice(success); after?.(); refresh(); }
    catch (e) { setError(e instanceof Error ? e.message : "No se pudo completar la acción."); }
    finally { setBusy(false); }
  }

  if (property.error) {
    const missing = property.error.status === 404;
    return (
      <Leaf><EmptyState icon={<Building2 size={18} />} title={missing ? "Propiedad no encontrada" : "No se pudo cargar la propiedad"}>
        {missing ? "Puede haberse eliminado o no pertenecer a tu organización." : property.error.message}
        <Link href="/properties" className="mt-3 block font-medium text-accent hover:underline">Volver a propiedades</Link>
      </EmptyState></Leaf>
    );
  }
  const p = property.data;
  const archived = p?.status === "Archived";
  const cols = showRent
    ? "md:grid-cols-[90px_minmax(0,1fr)_100px_130px_150px_90px]"
    : "md:grid-cols-[90px_minmax(0,1fr)_100px_150px_90px]";

  return (
    <div className="flex flex-col gap-6">
      <div>
        <Link href="/properties" className="mb-3 inline-flex items-center gap-1.5 text-sm text-ink-soft hover:text-ink"><ArrowLeft size={14} aria-hidden /> Propiedades</Link>
        {!p ? <Skeleton className="h-16 w-80" /> : (
          <div className="flex flex-wrap items-start justify-between gap-4">
            <div className="min-w-0">
              <div className="flex items-center gap-3">
                <h1 className="truncate text-2xl font-semibold tracking-tight">{p.name}</h1>
                {archived && <Badge>Archivada</Badge>}
              </div>
              <p className="mt-1 text-sm text-ink-soft">{p.address}</p>
              {p.notes && <p className="mt-2 max-w-prose text-sm text-ink-soft">{p.notes}</p>}
            </div>
            {admin && (
              <div className="flex gap-2">
                <Button variant="secondary" onClick={() => setPanel(panel === "edit" ? null : "edit")}><Pencil size={15} aria-hidden /> Editar</Button>
                {archived
                  ? <Button variant="secondary" loading={busy} onClick={() => run(() => api(`/properties/${id}`, { method: "PATCH", body: { status: "Active" } }), "Propiedad restaurada. Sus unidades siguen archivadas hasta que las reactives.")}><RotateCcw size={15} aria-hidden /> Restaurar</Button>
                  : <Button variant="danger" onClick={() => setPanel(panel === "archive" ? null : "archive")}><Archive size={15} aria-hidden /> Archivar</Button>}
              </div>
            )}
          </div>
        )}
      </div>

      <ErrorMessage message={error} />
      <SuccessMessage message={notice} />

      {p && panel === "edit" && (
        <Leaf>
          <header className="px-5 pb-4 pt-5"><h2 className="text-[15px] font-semibold">Editar propiedad</h2></header>
          <div className="perforation" />
          <form className="flex flex-col gap-4 px-5 pb-5 pt-4" onSubmit={(e) => {
            e.preventDefault(); const f = new FormData(e.currentTarget);
            run(() => api(`/properties/${id}`, { method: "PATCH", body: { name: f.get("name"), address: f.get("address"), notes: f.get("notes") ?? "" } }), "Propiedad actualizada.", () => setPanel(null));
          }}>
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label="Nombre" name="name" defaultValue={p.name} maxLength={200} required />
              <Field label="Dirección" name="address" defaultValue={p.address} maxLength={500} required />
            </div>
            <Textarea label="Notas" name="notes" defaultValue={p.notes ?? ""} maxLength={2000} />
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" onClick={() => setPanel(null)}>Cancelar</Button>
              <Button type="submit" loading={busy}>Guardar cambios</Button>
            </div>
          </form>
        </Leaf>
      )}

      {p && panel === "archive" && (
        <Leaf>
          <header className="px-5 pb-4 pt-5">
            <h2 className="text-[15px] font-semibold">Archivar propiedad</h2>
            <p className="mt-0.5 text-sm text-ink-soft">
              Se conserva todo su historial. {p.unitCount > 0 ? `Sus ${p.unitCount} unidades también se archivarán` : "No tiene unidades activas"} y no admitirá unidades nuevas.
            </p>
          </header>
          <div className="perforation" />
          <form className="flex flex-col gap-4 px-5 pb-5 pt-4" onSubmit={(e) => {
            e.preventDefault(); const reason = String(new FormData(e.currentTarget).get("reason") ?? "").trim();
            run(() => api(`/properties/${id}`, { method: "PATCH", body: { status: "Archived", reason: reason || null } }), "Propiedad archivada.", () => setPanel(null));
          }}>
            <Field label="Motivo" name="reason" hint="Opcional. Queda registrado en la auditoría." maxLength={1000} />
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" onClick={() => setPanel(null)}>Cancelar</Button>
              <Button type="submit" variant="danger" loading={busy}>Archivar propiedad</Button>
            </div>
          </form>
        </Leaf>
      )}

      <Leaf className="overflow-hidden">
        <header className="flex items-center justify-between gap-4 px-5 py-4">
          <div>
            <h2 className="text-[15px] font-semibold">Unidades</h2>
            <p className="text-sm text-ink-soft">{p ? `${p.unitCount} ${p.unitCount === 1 ? "activa" : "activas"}` : " "}</p>
          </div>
          {admin && !archived && p && panel !== "addUnit" && (
            <Button size="sm" onClick={() => setPanel("addUnit")}><Plus size={15} aria-hidden /> Agregar unidad</Button>
          )}
        </header>

        {panel === "addUnit" && (
          <>
            <div className="perforation" />
            <form className="grid items-end gap-4 px-5 pb-5 pt-4 sm:grid-cols-2 lg:grid-cols-[110px_minmax(0,1fr)_100px_150px_auto]" onSubmit={(e) => {
              e.preventDefault(); const form = e.currentTarget; const f = new FormData(form);
              run(() => api(`/properties/${id}/units`, { body: { code: f.get("code"), type: f.get("type"), bedrooms: f.get("bedrooms") === "" ? null : Number(f.get("bedrooms")), baseRent: Number(f.get("baseRent") || 0) } }),
                `Unidad ${String(f.get("code")).trim().toUpperCase()} creada.`, () => form.reset());
            }}>
              <Field label="Código" name="code" placeholder="101" maxLength={30} required />
              <Select label="Tipo" name="type" defaultValue="Apartment">{unitTypes.map((t) => <option key={t} value={t}>{unitTypeLabels[t]}</option>)}</Select>
              <Field label="Recámaras" name="bedrooms" type="number" min={0} max={50} />
              <Field label={`Renta base (${currency})`} name="baseRent" type="number" min={0} step="0.01" required />
              <div className="flex gap-2">
                <Button type="submit" loading={busy}>Agregar</Button>
                <Button type="button" variant="ghost" onClick={() => setPanel(null)}>Cerrar</Button>
              </div>
            </form>
          </>
        )}

        <div className="grid gap-3 border-y border-rule p-4 sm:grid-cols-[minmax(0,1fr)_200px]">
          <SearchInput label="Buscar unidades" placeholder="Buscar por código" value={search} onChange={(e) => setSearch(e.target.value)} />
          <Select aria-label="Estado de la unidad" value={status} onChange={(e) => { setStatus(e.target.value as "" | UnitStatus); setPage(1); }}>
            <option value="">Todos los estados</option>
            {(Object.keys(unitStatusLabels) as UnitStatus[]).map((s) => <option key={s} value={s}>{unitStatusLabels[s]}</option>)}
          </Select>
        </div>

        <div aria-hidden className={`hidden gap-4 border-b border-rule bg-paper px-5 py-2.5 text-xs font-medium text-ink-soft md:grid ${cols}`}>
          <span>Código</span><span>Tipo</span><span>Recámaras</span>{showRent && <span className="text-right">Renta base</span>}<span>Estado</span><span />
        </div>
        {units.error && <div className="p-5"><ErrorMessage message={units.error.message} /></div>}
        {!units.data && !units.error && <div className="flex flex-col gap-3 p-5">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-9" />)}</div>}
        {units.data && units.data.items.length === 0 && (
          <EmptyState icon={<DoorOpen size={18} />} title={debounced || status ? "Sin resultados" : "Aún no hay unidades"}>
            {debounced || status ? "Prueba con otro código o estado." : admin && !archived ? "Agrega las unidades rentables de esta propiedad." : undefined}
          </EmptyState>
        )}
        {units.data && units.data.items.length > 0 && (
          <ul className={units.loading ? "opacity-60 transition-opacity" : "transition-opacity"}>
            {units.data.items.map((u) => (
              <li key={u.id} className="border-b border-rule last:border-b-0">
                <div className={`grid items-center gap-x-4 gap-y-1 px-5 py-3.5 ${cols}`}>
                  <span className="font-mono text-sm font-medium">{u.code}</span>
                  <span className="text-sm">{unitTypeLabels[u.type]}</span>
                  <span className="text-sm text-ink-soft"><span className="md:hidden">Recámaras: </span>{u.bedrooms != null ? `${u.bedrooms} rec.` : "—"}</span>
                  {showRent && <span className="text-sm font-medium md:text-right"><span className="font-normal text-ink-soft md:hidden">Renta base: </span>{u.baseRent != null ? formatMoney(u.baseRent, currency) : "—"}</span>}
                  <div><Badge tone={statusTone[u.status]}>{unitStatusLabels[u.status]}</Badge></div>
                  <div className="md:text-right">
                    {admin && <Button variant="ghost" size="sm" onClick={() => setEditingUnit(editingUnit === u.id ? null : u.id)}>{editingUnit === u.id ? "Cerrar" : "Editar"}</Button>}
                  </div>
                </div>
                {editingUnit === u.id && (
                  <form className="grid items-end gap-4 border-t border-rule bg-paper px-5 py-4 sm:grid-cols-2 lg:grid-cols-[minmax(0,1fr)_100px_150px_170px_auto]" onSubmit={(e) => {
                    e.preventDefault(); const f = new FormData(e.currentTarget);
                    run(() => api(`/properties/${id}/units/${u.id}`, { method: "PATCH", body: {
                      type: f.get("type"), bedrooms: f.get("bedrooms") === "" ? undefined : Number(f.get("bedrooms")), baseRent: Number(f.get("baseRent")), status: f.get("status") } }),
                      `Unidad ${u.code} actualizada.`, () => setEditingUnit(null));
                  }}>
                    <Select label="Tipo" name="type" defaultValue={u.type}>{unitTypes.map((t) => <option key={t} value={t}>{unitTypeLabels[t]}</option>)}</Select>
                    <Field label="Recámaras" name="bedrooms" type="number" min={0} max={50} defaultValue={u.bedrooms ?? ""} />
                    <Field label={`Renta base (${currency})`} name="baseRent" type="number" min={0} step="0.01" defaultValue={u.baseRent} required />
                    <Select label="Estado" name="status" defaultValue={u.status}>
                      {editableStatuses.map((s) => <option key={s} value={s}>{unitStatusLabels[s]}</option>)}
                    </Select>
                    <Button type="submit" loading={busy}>Guardar</Button>
                  </form>
                )}
              </li>
            ))}
          </ul>
        )}
        {units.data && <Pager page={units.data.page} pageSize={units.data.pageSize} total={units.data.total} onPage={setPage} />}
      </Leaf>
    </div>
  );
}
