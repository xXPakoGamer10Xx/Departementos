"use client";
import { Banknote, Home, Wrench } from "lucide-react";
import Link from "next/link";
import { ChargeRow } from "@/components/portal-ui";
import { Badge, EmptyState, ErrorMessage, Leaf, Skeleton } from "@/components/ui";
import { api, formatDate, formatMoney, formatPeriod, type Paged, type PortalCharge, type PortalSummary } from "@/lib/api";
import { DownloadButton } from "@/components/download-button";
import { useQuery } from "@/lib/use-query";

export default function PortalHomePage() {
  const summary = useQuery("portal:summary", () => api<PortalSummary>("/portal/summary"));
  const pending = useQuery("portal:pending", () => api<Paged<PortalCharge>>("/portal/charges?pendingOnly=true&pageSize=5"));
  const s = summary.data;

  return (
    <div className="flex flex-col gap-6">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">{s ? `Hola, ${s.tenantName.split(" ")[0]}` : "Hola"}</h1>
        <p className="mt-1 text-sm text-ink-soft">Tu cuenta al día de hoy.</p>
      </div>
      <ErrorMessage message={summary.error?.message ?? null} />
      {!s && !summary.error && <Leaf><div className="flex flex-col gap-3 p-5"><Skeleton className="h-6 w-40" /><Skeleton className="h-10 w-56" /></div></Leaf>}

      {s && (
        <Leaf>
          <div className="px-5 pb-5 pt-5">
            <p className="text-sm text-ink-soft">Saldo por pagar</p>
            <p className="mt-1 font-mono text-4xl font-semibold tabular-nums">{formatMoney(s.totalDue, s.currency)}</p>
            <div className="mt-3 flex flex-wrap items-center gap-2">
              {s.overdueCount > 0
                ? <Badge tone="stamp">{formatMoney(s.overdueAmount, s.currency)} vencido</Badge>
                : s.totalDue > 0 ? <Badge tone="pending">Sin atrasos</Badge> : <Badge tone="ledger">Estás al corriente</Badge>}
              {s.creditBalance > 0 && <Badge tone="accent">{formatMoney(s.creditBalance, s.currency)} a tu favor</Badge>}
            </div>
            {s.nextCharge && (
              <p className="mt-4 text-sm text-ink-soft">
                Próximo vencimiento: <strong className="font-medium text-ink">{s.nextCharge.description}</strong> — {formatDate(s.nextCharge.dueDate)}
              </p>
            )}
          </div>
          <div className="perforation" />
          <div className="flex flex-wrap items-center justify-between gap-3 px-5 py-3">
            <p className="text-xs text-ink-faint">Tu saldo se actualiza cuando la administración confirma tu pago.</p>
            <Link href="/portal/pay" className="inline-flex h-9 items-center gap-2 rounded-md bg-accent px-4 text-sm font-medium text-white hover:bg-accent-hover"><Banknote size={15} aria-hidden /> Pagar</Link>
          </div>
        </Leaf>
      )}

      {s && (
        <Leaf>
          <header className="px-5 py-4"><h2 className="text-[15px] font-semibold">Tu departamento</h2></header>
          <div className="perforation" />
          {s.lease ? (
            <dl className="grid gap-x-6 gap-y-3 px-5 py-4 text-sm sm:grid-cols-2">
              <div><dt className="text-xs text-ink-faint">Propiedad</dt><dd className="font-medium">{s.lease.propertyName}</dd><dd className="text-ink-soft">{s.lease.propertyAddress}</dd></div>
              <div><dt className="text-xs text-ink-faint">Unidad</dt><dd className="font-mono font-medium">{s.lease.unitCode}</dd></div>
              <div><dt className="text-xs text-ink-faint">Renta mensual</dt><dd className="font-medium">{formatMoney(s.lease.monthlyRent, s.currency)}</dd><dd className="text-ink-soft">Se paga el día {s.lease.dueDay}</dd></div>
              <div><dt className="text-xs text-ink-faint">Vigencia</dt><dd className="font-medium">{formatPeriod(s.lease)}</dd></div>
              {s.hasContract && <div className="sm:col-span-2"><DownloadButton size="sm" path="/portal/contract.pdf">Mi contrato (PDF)</DownloadButton></div>}
            </dl>
          ) : (
            <EmptyState icon={<Home size={18} />} title="No tienes un contrato vigente">Si crees que es un error, contacta a la administración.</EmptyState>
          )}
        </Leaf>
      )}

      <Leaf className="overflow-hidden">
        <header className="flex items-center justify-between px-5 py-4">
          <h2 className="text-[15px] font-semibold">Pendiente de pago</h2>
          <Link href="/portal/account" className="text-sm font-medium text-accent hover:underline">Ver todo</Link>
        </header>
        <div className="perforation" />
        {!pending.data && !pending.error && <div className="p-5"><Skeleton className="h-10" /></div>}
        {pending.data?.items.length === 0 && <p className="px-5 py-4 text-sm text-ink-soft">No tienes cargos pendientes.</p>}
        {pending.data && pending.data.items.length > 0 && <ul>{pending.data.items.map((c) => <ChargeRow key={c.id} c={c} currency={s?.currency ?? "MXN"} />)}</ul>}
      </Leaf>

      <Link href="/portal/issues/new" className="flex items-center gap-3 rounded-lg border border-rule bg-leaf px-5 py-4 text-sm font-medium hover:bg-paper">
        <Wrench size={18} className="text-accent" aria-hidden /> ¿Algo se descompuso? Repórtalo aquí
      </Link>
    </div>
  );
}
