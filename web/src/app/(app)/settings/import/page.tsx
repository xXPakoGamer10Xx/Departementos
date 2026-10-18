"use client";
import { ArrowLeft, FileSpreadsheet, Lock } from "lucide-react";
import Link from "next/link";
import { useRef, useState } from "react";
import { useMe } from "@/components/me-context";
import { Badge, Button, EmptyState, ErrorMessage, Leaf, Select, SuccessMessage } from "@/components/ui";
import { api, apiUpload, canManage, formatDate, formatMoney, type ImportPreview, type ImportResult, type Paged } from "@/lib/api";
import { downloadFile } from "@/lib/download";
import { useQuery } from "@/lib/use-query";

type Property = { id: string; name: string };
const actionTone = { Create: "ledger", AlreadyImported: "neutral", Error: "stamp" } as const;
const actionLabel = { Create: "Se creará", AlreadyImported: "Ya existe", Error: "Con error" } as const;

/**
 * Brings the tenants-and-contracts spreadsheet into Habita in two steps: a preview that changes nothing, then a confirmation that saves everything or nothing.
 * The file is sent again on confirming (it is never stored), together with the fingerprint of the preview, so what is saved is exactly what was shown.
 */
export default function ImportPage() {
  const me = useMe();
  const allowed = canManage(me.activeRole);
  const currency = me.organizations.find((o) => o.id === me.activeOrganizationId)?.currency ?? "MXN";
  const properties = useQuery("import-properties", () => (allowed ? api<Paged<Property>>("/properties?status=Active&pageSize=100") : Promise.resolve(null)));
  const fileRef = useRef<HTMLInputElement>(null);
  const [propertyId, setPropertyId] = useState("");
  const [createUnits, setCreateUnits] = useState(true);
  const [activate, setActivate] = useState(true);
  const [skip, setSkip] = useState(false);
  const [preview, setPreview] = useState<ImportPreview | null>(null);
  const [result, setResult] = useState<ImportResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const form = (hash?: string) => {
    const f = new FormData();
    f.set("file", fileRef.current!.files![0]); f.set("propertyId", propertyId);
    f.set("createMissingUnits", String(createUnits)); f.set("activateLeases", String(activate)); f.set("skipErrors", String(skip));
    if (hash) f.set("planHash", hash);
    return f;
  };
  async function run(kind: "preview" | "confirm") {
    if (!fileRef.current?.files?.length) { setError("Elige el archivo de Excel o CSV."); return; }
    if (!propertyId) { setError("Elige la propiedad a la que pertenecen los departamentos."); return; }
    setBusy(true); setError(null);
    try {
      if (kind === "preview") { setResult(null); setPreview(await apiUpload<ImportPreview>("/imports/preview", form())); }
      else { setResult(await apiUpload<ImportResult>("/imports/confirm", form(preview!.planHash))); setPreview(null); if (fileRef.current) fileRef.current.value = ""; }
    } catch (e) { setError(e instanceof Error ? e.message : "No se pudo procesar el archivo."); }
    finally { setBusy(false); }
  }

  if (!allowed) return <Leaf><EmptyState icon={<Lock size={18} />} title="No tienes permiso para importar">Solo los propietarios y administradores importan datos.</EmptyState></Leaf>;
  const changed = () => { setPreview(null); setResult(null); };

  return (
    <div className="flex flex-col gap-6">
      <div>
        <Link href="/settings" className="mb-3 inline-flex items-center gap-1.5 text-sm text-ink-soft hover:text-ink"><ArrowLeft size={14} aria-hidden /> Ajustes</Link>
        <h1 className="text-2xl font-semibold tracking-tight">Importar inquilinos y contratos</h1>
        <p className="mt-1 text-sm text-ink-soft">Sube tu Excel (.xlsx) o un CSV. Primero verás una vista previa; no se guarda nada hasta que confirmes. El archivo no se conserva.</p>
      </div>
      <ErrorMessage message={error ?? properties.error?.message ?? null} />
      {result && (
        <SuccessMessage message={`Listo: ${result.leasesCreated} contratos creados (${result.tenantsCreated} inquilinos y ${result.unitsCreated} unidades nuevos${result.leasesActivated ? `, ${result.leasesActivated} activados` : ""}). ${result.alreadyImported ? `${result.alreadyImported} ya existían. ` : ""}${result.skippedWithErrors ? `${result.skippedWithErrors} filas con error se omitieron.` : ""}`} />
      )}
      <Leaf>
        <header className="px-5 pb-4 pt-5"><h2 className="text-[15px] font-semibold">1. Archivo y opciones</h2>
          <p className="mt-0.5 text-sm text-ink-soft">Columnas: Nombre, Depto, Renta, Inicio (obligatorias); Fecha de pago, Término, Fiador, Teléfonos, Correo, Depósito, Observaciones (opcionales). <button type="button" className="font-medium text-accent hover:underline" onClick={() => downloadFile("/imports/template.csv", "Plantilla de inquilinos.csv").catch((e) => setError(e.message))}>Descargar plantilla</button></p></header>
        <div className="perforation" />
        <div className="flex flex-col gap-4 px-5 pb-5 pt-4">
          <div className="flex flex-col gap-1.5">
            <label htmlFor="imp-file" className="text-sm font-medium">Archivo</label>
            <input id="imp-file" ref={fileRef} type="file" accept=".xlsx,.csv,text/csv" onChange={changed} className="text-sm file:mr-3 file:rounded-md file:border file:border-rule-strong file:bg-leaf file:px-3 file:py-2 file:text-sm" />
          </div>
          <Select label="Propiedad" value={propertyId} onChange={(e) => { setPropertyId(e.target.value); changed(); }}>
            <option value="">Elige una propiedad…</option>
            {properties.data?.items.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
          </Select>
          <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={createUnits} onChange={(e) => { setCreateUnits(e.target.checked); changed(); }} /> Crear los departamentos que no existan</label>
          <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={activate} onChange={(e) => { setActivate(e.target.checked); changed(); }} /> Activar los contratos (si no, quedan como borrador)</label>
          <div><Button loading={busy && !preview} onClick={() => run("preview")}><FileSpreadsheet size={15} aria-hidden /> Revisar archivo</Button></div>
        </div>
      </Leaf>

      {preview && (
        <Leaf className="overflow-hidden">
          <header className="px-5 py-4">
            <h2 className="text-[15px] font-semibold">2. Vista previa · hoja «{preview.sheetName}»</h2>
            <p className="mt-0.5 text-sm text-ink-soft">{preview.toCreate} por crear · {preview.alreadyImported} ya existentes · {preview.errors} con error · {preview.newTenants} inquilinos y {preview.newUnits} departamentos nuevos.
              {preview.unknownColumns.length > 0 && <> Columnas que no se usan: {preview.unknownColumns.join(", ")}.</>}</p>
          </header>
          <div className="perforation" />
          <div className="overflow-x-auto">
            <table className="w-full text-left text-sm">
              <thead className="text-ink-soft"><tr><th className="px-5 py-2">Fila</th><th className="px-2 py-2">Inquilino</th><th className="px-2 py-2">Depto</th><th className="px-2 py-2">Renta</th><th className="px-2 py-2">Inicio</th><th className="px-2 py-2">Resultado</th></tr></thead>
              <tbody>{preview.rows.map((r) => (
                <tr key={r.row} className="border-t border-rule align-top">
                  <td className="px-5 py-2 text-ink-faint">{r.row}</td>
                  <td className="px-2 py-2">{r.name || "—"}{r.guarantorName && <span className="block text-ink-soft">Fiador: {r.guarantorName}</span>}</td>
                  <td className="px-2 py-2 font-mono">{r.unitCode}{r.newUnit && <span className="block font-sans text-xs text-ink-faint">nuevo</span>}</td>
                  <td className="px-2 py-2">{r.monthlyRent != null ? formatMoney(r.monthlyRent, currency) : "—"}</td>
                  <td className="px-2 py-2">{r.startDate ? formatDate(r.startDate) : "—"}</td>
                  <td className="px-2 py-2"><Badge tone={actionTone[r.action]}>{actionLabel[r.action]}</Badge>
                    {[...r.errors, ...r.warnings].map((m, n) => <span key={n} className={`block text-xs ${n < r.errors.length ? "text-stamp" : "text-ink-soft"}`}>{m}</span>)}</td>
                </tr>))}</tbody>
            </table>
          </div>
          <div className="perforation" />
          <div className="flex flex-wrap items-center gap-4 px-5 py-4">
            {preview.errors > 0 && <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={skip} onChange={(e) => setSkip(e.target.checked)} /> Omitir las {preview.errors} filas con error e importar el resto</label>}
            <Button loading={busy} disabled={preview.toCreate === 0 || (preview.errors > 0 && !skip)} onClick={() => run("confirm")}>Importar {preview.toCreate} contratos</Button>
          </div>
        </Leaf>
      )}
    </div>
  );
}
