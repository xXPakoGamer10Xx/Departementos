"use client";
import { ClipboardList, Plus, Trash2 } from "lucide-react";
import { useState } from "react";
import { Badge, Button, ErrorMessage, Field, Leaf, Select, Skeleton, SuccessMessage } from "@/components/ui";
import { api, conditionLabels, type InventoryItem, type ItemCondition, type LeaseStatus } from "@/lib/api";
import { useQuery } from "@/lib/use-query";

type Draft = { id?: string; name: string; quantity: number; condition: ItemCondition; notes: string };
const tone: Record<ItemCondition, "ledger" | "pending" | "stamp"> = { Good: "ledger", Fair: "pending", Poor: "stamp" };
const toDraft = (i: InventoryItem): Draft => ({ id: i.id, name: i.name, quantity: i.quantity, condition: i.condition, notes: i.notes ?? "" });

/** The acta de entrega: what the unit is handed over with, and in what condition. When the lease ends, the condition it comes back in is recorded beside it. */
export function LeaseInventory({ leaseId, status, canEdit }: { leaseId: string; status: LeaseStatus; canEdit: boolean }) {
  const [version, setVersion] = useState(0);
  const items = useQuery(`inv:${leaseId}:${version}`, () => api<InventoryItem[]>(`/leases/${leaseId}/inventory`));
  const [mode, setMode] = useState<"view" | "edit" | "return">("view");
  const [draft, setDraft] = useState<Draft[]>([]);
  const [returns, setReturns] = useState<Record<string, { condition: ItemCondition; notes: string }>>({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const closed = status === "Ended";
  const data = items.data;

  async function save(path: string, method: "PUT" | "POST", body: unknown, message: string) {
    setBusy(true); setError(null); setNotice(null);
    try { await api(`/leases/${leaseId}/inventory${path}`, { method, body }); setNotice(message); setMode("view"); setVersion((v) => v + 1); }
    catch (e) { setError(e instanceof Error ? e.message : "No se pudo guardar."); }
    finally { setBusy(false); }
  }
  const update = (i: number, patch: Partial<Draft>) => setDraft((d) => d.map((x, n) => (n === i ? { ...x, ...patch } : x)));

  return (
    <Leaf className="overflow-hidden">
      <header className="flex flex-wrap items-center justify-between gap-2 px-5 py-4">
        <div><h2 className="text-[15px] font-semibold">Inventario de entrega</h2><p className="mt-0.5 text-sm text-ink-soft">Lo que se entrega con la unidad y su estado.</p></div>
        {canEdit && mode === "view" && data && (
          <div className="flex gap-2">
            {!closed && <Button variant="secondary" onClick={() => { setDraft(data.map(toDraft)); setMode("edit"); }}><ClipboardList size={15} aria-hidden /> {data.length ? "Editar" : "Capturar"}</Button>}
            {closed && data.length > 0 && <Button variant="secondary" onClick={() => { setReturns(Object.fromEntries(data.map((i) => [i.id, { condition: i.returnCondition ?? i.condition, notes: i.returnNotes ?? "" }]))); setMode("return"); }}>Registrar devolución</Button>}
          </div>
        )}
      </header>
      <div className="perforation" />
      <div className="flex flex-col gap-3 px-5 py-4">
        <ErrorMessage message={error ?? items.error?.message ?? null} /><SuccessMessage message={notice} />
        {!data && !items.error && <Skeleton className="h-10" />}
        {data && mode === "view" && (data.length === 0
          ? <p className="text-sm text-ink-soft">{closed ? "No se capturó inventario de entrega." : "Aún no hay inventario. Captúralo al entregar la unidad."}</p>
          : <ul className="divide-y divide-rule">{data.map((i) => (
            <li key={i.id} className="flex flex-wrap items-center justify-between gap-2 py-2 text-sm">
              <span className="min-w-0">{i.quantity} × {i.name}{i.notes && <span className="block text-ink-soft">{i.notes}</span>}</span>
              <span className="flex items-center gap-2"><Badge tone={tone[i.condition]}>{conditionLabels[i.condition]}</Badge>
                {i.returnCondition && <span className="text-ink-soft">→ regresó <Badge tone={tone[i.returnCondition]}>{conditionLabels[i.returnCondition]}</Badge></span>}</span>
              {i.returnNotes && <span className="w-full text-ink-soft">Al regresar: {i.returnNotes}</span>}
            </li>))}</ul>)}

        {mode === "edit" && (
          <form noValidate className="flex flex-col gap-3" onSubmit={(e) => {
            e.preventDefault();
            if (draft.some((d) => !d.name.trim())) { setError("Cada artículo necesita un nombre."); return; }
            save("", "PUT", { items: draft.map((d) => ({ id: d.id, name: d.name, quantity: d.quantity, condition: d.condition, notes: d.notes })) }, "Inventario guardado.");
          }}>
            {draft.map((d, i) => (
              <div key={d.id ?? `n${i}`} className="grid items-end gap-2 sm:grid-cols-[minmax(0,2fr)_80px_130px_minmax(0,2fr)_auto]">
                <Field label="Artículo" value={d.name} maxLength={100} onChange={(e) => update(i, { name: e.target.value })} />
                <Field label="Cant." type="number" min={1} max={999} value={d.quantity} onChange={(e) => update(i, { quantity: Number(e.target.value) })} />
                <Select label="Estado" value={d.condition} onChange={(e) => update(i, { condition: e.target.value as ItemCondition })}>
                  {(Object.keys(conditionLabels) as ItemCondition[]).map((c) => <option key={c} value={c}>{conditionLabels[c]}</option>)}
                </Select>
                <Field label="Notas" value={d.notes} maxLength={300} onChange={(e) => update(i, { notes: e.target.value })} />
                <Button type="button" variant="ghost" aria-label={`Quitar ${d.name || "artículo"}`} onClick={() => setDraft((x) => x.filter((_, n) => n !== i))}><Trash2 size={15} aria-hidden /></Button>
              </div>
            ))}
            <div className="flex flex-wrap justify-between gap-2">
              <Button type="button" variant="secondary" onClick={() => setDraft((x) => [...x, { name: "", quantity: 1, condition: "Good", notes: "" }])}><Plus size={15} aria-hidden /> Agregar artículo</Button>
              <div className="flex gap-2"><Button type="button" variant="ghost" onClick={() => setMode("view")}>Cancelar</Button><Button type="submit" loading={busy}>Guardar inventario</Button></div>
            </div>
          </form>
        )}

        {mode === "return" && data && (
          <form noValidate className="flex flex-col gap-3" onSubmit={(e) => {
            e.preventDefault();
            save("/return", "POST", { items: data.map((i) => ({ itemId: i.id, condition: returns[i.id].condition, notes: returns[i.id].notes })) }, "Devolución registrada.");
          }}>
            {data.map((i) => (
              <div key={i.id} className="grid items-end gap-2 sm:grid-cols-[minmax(0,2fr)_130px_minmax(0,2fr)]">
                <p className="pb-2 text-sm">{i.quantity} × {i.name} <span className="text-ink-faint">· entregado {conditionLabels[i.condition].toLowerCase()}</span></p>
                <Select label="Al regresar" value={returns[i.id].condition} onChange={(e) => setReturns((r) => ({ ...r, [i.id]: { ...r[i.id], condition: e.target.value as ItemCondition } }))}>
                  {(Object.keys(conditionLabels) as ItemCondition[]).map((c) => <option key={c} value={c}>{conditionLabels[c]}</option>)}
                </Select>
                <Field label="Notas" value={returns[i.id].notes} maxLength={300} onChange={(e) => setReturns((r) => ({ ...r, [i.id]: { ...r[i.id], notes: e.target.value } }))} />
              </div>
            ))}
            <div className="flex justify-end gap-2"><Button type="button" variant="ghost" onClick={() => setMode("view")}>Cancelar</Button><Button type="submit" loading={busy}>Guardar devolución</Button></div>
          </form>
        )}
      </div>
    </Leaf>
  );
}
