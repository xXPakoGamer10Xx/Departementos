"use client";
import { ChevronRight, FileSpreadsheet, FileText, Landmark, Lock } from "lucide-react";
import Link from "next/link";
import { useState } from "react";
import { useMe } from "@/components/me-context";
import { Button, EmptyState, ErrorMessage, Field, Leaf, Skeleton, SuccessMessage } from "@/components/ui";
import { api, canManage } from "@/lib/api";
import { useQuery } from "@/lib/use-query";

type Settings = { landlordName: string; landlordAddress: string; rentReminderDays: number[]; contractExpiryDays: number[]; reminderHour: number; graceDays: number; updatedAt: string | null };

const parseDays = (text: string): number[] => text.split(",").map((s) => s.trim()).filter(Boolean).map(Number);

export default function SettingsPage() {
  const me = useMe();
  const allowed = canManage(me.activeRole);
  const [version, setVersion] = useState(0);
  const { data, error: loadError } = useQuery(`settings:${version}`, () => (allowed ? api<Settings>("/settings") : Promise.reject({ message: "forbidden", status: 403 })));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  async function save(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    const rent = parseDays(String(f.get("rent"))), expiry = parseDays(String(f.get("expiry")));
    if ([...rent, ...expiry].some((n) => !Number.isInteger(n))) { setError("Escribe los días como números separados por comas, por ejemplo 3, 1, 0."); return; }
    const hour = Number(f.get("reminderHour"));
    if (!Number.isInteger(hour) || hour < 0 || hour > 23) { setError("La hora de los recordatorios va de 0 a 23."); return; }
    const grace = Number(f.get("graceDays"));
    if (!Number.isInteger(grace) || grace < 0 || grace > 30) { setError("Los días de gracia van de 0 a 30."); return; }
    setBusy(true); setError(null); setNotice(null);
    try {
      await api("/settings", { method: "PATCH", body: { landlordName: f.get("landlordName"), landlordAddress: f.get("landlordAddress"), rentReminderDays: rent, contractExpiryDays: expiry, reminderHour: hour, graceDays: grace } });
      setNotice("Ajustes guardados."); setVersion((v) => v + 1);
    } catch (err) { setError(err instanceof Error ? err.message : "No se pudo guardar."); }
    finally { setBusy(false); }
  }

  if (!allowed || (loadError as { status?: number } | null)?.status === 403) {
    return <Leaf><EmptyState icon={<Lock size={18} />} title="No tienes permiso para ver esta sección">Solo los propietarios y administradores cambian los ajustes.</EmptyState></Leaf>;
  }

  return (
    <div className="flex flex-col gap-6">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Ajustes</h1>
        <p className="mt-1 text-sm text-ink-soft">Datos del arrendador y cuándo se avisa a inquilinos y equipo.</p>
      </div>
      <Link href="/settings/bank-accounts" className="flex items-center gap-3 rounded-lg border border-rule bg-leaf px-5 py-4 text-sm font-medium hover:bg-paper">
        <Landmark size={18} className="text-accent" aria-hidden /> Cuentas bancarias para recibir transferencias <ChevronRight size={16} className="ml-auto text-ink-faint" aria-hidden />
      </Link>
      <Link href="/settings/contract" className="flex items-center gap-3 rounded-lg border border-rule bg-leaf px-5 py-4 text-sm font-medium hover:bg-paper">
        <FileText size={18} className="text-accent" aria-hidden /> Plantilla de contrato (Word) <ChevronRight size={16} className="ml-auto text-ink-faint" aria-hidden />
      </Link>
      <Link href="/settings/import" className="flex items-center gap-3 rounded-lg border border-rule bg-leaf px-5 py-4 text-sm font-medium hover:bg-paper">
        <FileSpreadsheet size={18} className="text-accent" aria-hidden /> Importar inquilinos y contratos desde Excel <ChevronRight size={16} className="ml-auto text-ink-faint" aria-hidden />
      </Link>
      <ErrorMessage message={error ?? loadError?.message ?? null} />
      <SuccessMessage message={notice} />
      {!data && !loadError && <Leaf><div className="flex flex-col gap-4 p-5">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-10" />)}</div></Leaf>}
      {data && (
        <form key={data.updatedAt ?? "new"} onSubmit={save} className="flex flex-col gap-6">
          <Leaf>
            <header className="px-5 pb-4 pt-5"><h2 className="text-[15px] font-semibold">Arrendador</h2><p className="mt-0.5 text-sm text-ink-soft">Aparece en contratos y recibos.</p></header>
            <div className="perforation" />
            <div className="grid gap-4 px-5 pb-5 pt-4">
              <Field label="Nombre completo" name="landlordName" defaultValue={data.landlordName} maxLength={200} />
              <Field label="Domicilio" name="landlordAddress" defaultValue={data.landlordAddress} maxLength={500} />
            </div>
          </Leaf>
          <Leaf>
            <header className="px-5 pb-4 pt-5"><h2 className="text-[15px] font-semibold">Recordatorios</h2><p className="mt-0.5 text-sm text-ink-soft">Días de anticipación, separados por comas. Deja vacío para no avisar. Los avisos de renta llegan al inquilino; los cargos vencidos y los contratos por terminar, también al equipo.</p></header>
            <div className="perforation" />
            <div className="grid gap-4 px-5 pb-5 pt-4 sm:grid-cols-2">
              <Field label="Antes del vencimiento de una renta" name="rent" defaultValue={data.rentReminderDays.join(", ")} hint="0 es el mismo día. Ejemplo: 3, 1, 0" />
              <Field label="Antes de que termine un contrato" name="expiry" defaultValue={data.contractExpiryDays.join(", ")} hint="Ejemplo: 30, 7, 1" />
              <Field label="Hora de los recordatorios" name="reminderHour" type="number" min={0} max={23} step={1} defaultValue={data.reminderHour} hint="Hora local de tu organización (0 a 23). Se envían una vez al día, a partir de esta hora." />
            </div>
          </Leaf>
          <Leaf>
            <header className="px-5 pb-4 pt-5"><h2 className="text-[15px] font-semibold">Días de gracia</h2><p className="mt-0.5 text-sm text-ink-soft">Cuántos días después de la fecha de pago un cargo sigue contando como pendiente. Pasado ese plazo se marca como vencido en cargos, reportes, tablero y avisos.</p></header>
            <div className="perforation" />
            <div className="grid gap-4 px-5 pb-5 pt-4 sm:grid-cols-2">
              <Field label="Días de gracia" name="graceDays" type="number" min={0} max={30} step={1} defaultValue={data.graceDays} hint="0 = vence al día siguiente de la fecha de pago. Máximo 30." />
            </div>
          </Leaf>
          <div><Button type="submit" loading={busy}>Guardar ajustes</Button></div>
        </form>
      )}
    </div>
  );
}
