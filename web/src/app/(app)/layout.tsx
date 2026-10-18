"use client";
import { ArrowLeftRight, Banknote, BarChart3, CalendarClock, FileCheck2, Home, Settings, Building2, FileText, HandCoins, LayoutDashboard, LogOut, Menu, QrCode, Receipt, ScrollText, UserRound, Users, Wrench, X } from "lucide-react";
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, useState, type ReactNode } from "react";
import { MeContext } from "@/components/me-context";
import { NotificationBell, NotificationsProvider } from "@/components/notifications";
import { Avatar, Badge, Skeleton, Wordmark, cx } from "@/components/ui";
import { api, canManage, canReadFinance, canReadLeases, canReadReports, canReadTenants, canWritePayments, homePath, isStaff, isTenant, roleLabels, type Me } from "@/lib/api";

const nav = [
  { href: "/portal", label: "Inicio", icon: Home, visible: isTenant },
  { href: "/portal/account", label: "Estado de cuenta", icon: Receipt, visible: isTenant },
  { href: "/portal/pay", label: "Pagar", icon: Banknote, visible: isTenant },
  { href: "/portal/payments", label: "Mis pagos", icon: HandCoins, visible: isTenant },
  { href: "/portal/issues", label: "Reportar falla", icon: Wrench, visible: isTenant },
  { href: "/dashboard", label: "Resumen", icon: LayoutDashboard, visible: isStaff },
  { href: "/properties", label: "Propiedades", icon: Building2, visible: isStaff },
  { href: "/tenants", label: "Inquilinos", icon: UserRound, visible: canReadTenants },
  { href: "/leases", label: "Contratos", icon: FileText, visible: canReadLeases },
  { href: "/charges", label: "Cargos", icon: Receipt, visible: canReadFinance },
  { href: "/payments", label: "Pagos", icon: HandCoins, visible: canReadFinance },
  { href: "/claims", label: "Reportes de pago", icon: FileCheck2, visible: canWritePayments },
  { href: "/collect", label: "Cobrar en efectivo", icon: QrCode, visible: canWritePayments },
  { href: "/promises", label: "Promesas de pago", icon: CalendarClock, visible: canReadFinance },
  { href: "/maintenance", label: "Mantenimiento", icon: Wrench, visible: isStaff },
  { href: "/reports", label: "Reportes", icon: BarChart3, visible: canReadReports },
  { href: "/members", label: "Miembros", icon: Users, visible: canManage },
  { href: "/audit", label: "Auditoría", icon: ScrollText, visible: canManage },
  { href: "/settings", label: "Ajustes", icon: Settings, visible: canManage },
];

// Exact match or a child route: "/portal/pay" must not light up on "/portal/payments".
const isActive = (pathname: string, href: string) => pathname === href || (href !== "/portal" && pathname.startsWith(`${href}/`));

export default function AppLayout({ children }: { children: ReactNode }) {
  const router = useRouter();
  const pathname = usePathname();
  const [me, setMe] = useState<Me | null>(null);
  const [open, setOpen] = useState(false);

  useEffect(() => {
    api<Me>("/me")
      .then((m) => (m.activeOrganizationId ? setMe(m) : router.replace("/select-organization")))
      .catch(() => router.replace("/login"));
  }, [router]);

  // A tenant login only lives in /portal; a collector has no dashboard. Send them home instead of showing a screen that would answer 403.
  const role = me?.activeRole ?? null;
  const misplaced = me && ((role === "Tenant" && !pathname.startsWith("/portal") && pathname !== "/notifications") || (role === "Collector" && (pathname.startsWith("/dashboard") || pathname.startsWith("/properties") || pathname.startsWith("/maintenance"))));
  useEffect(() => { if (misplaced) router.replace(homePath(role)); }, [misplaced, role, router]);

  async function logout() {
    await api("/auth/logout", { method: "POST" });
    router.push("/login");
  }

  if (!me) {
    return (
      <div className="flex min-h-screen">
        <aside className="hidden w-[248px] shrink-0 border-r border-rule bg-paper-deep p-5 lg:block"><Skeleton className="h-8 w-28" /></aside>
        <main className="flex-1 p-8"><Skeleton className="h-8 w-48" /></main>
      </div>
    );
  }

  if (misplaced) return null;
  const org = me.organizations.find((o) => o.id === me.activeOrganizationId);

  const rail = (
    <div className="flex h-full flex-col">
      <div className="flex items-center justify-between border-b border-rule px-5 py-4">
        <Wordmark />
        <NotificationBell />
      </div>
      <div className="px-5 pb-1 pt-5">
        <p className="truncate font-semibold" title={org?.name}>{org?.name}</p>
        <p className="font-mono text-xs text-ink-faint">N.º {me.activeOrganizationId?.slice(0, 6).toUpperCase()}</p>
      </div>
      <nav aria-label="Principal" className="flex flex-1 flex-col gap-0.5 px-3 py-4">
        {nav.filter((n) => n.visible(me.activeRole)).map((n) => {
          const active = isActive(pathname, n.href);
          return (
            <Link key={n.href} href={n.href} onClick={() => setOpen(false)} aria-current={active ? "page" : undefined}
              className={cx("flex h-9 items-center gap-2.5 rounded-md px-2.5 text-sm font-medium transition-colors",
                active ? "bg-leaf text-accent ring-1 ring-rule" : "text-ink-soft hover:bg-leaf/60 hover:text-ink")}>
              <n.icon size={16} aria-hidden /> {n.label}
            </Link>
          );
        })}
      </nav>
      <div className="border-t border-rule p-4">
        <div className="flex items-center gap-2.5">
          <Avatar name={me.user.fullName} />
          <div className="min-w-0">
            <p className="truncate text-sm font-medium">{me.user.fullName}</p>
            <p className="truncate text-xs text-ink-faint">{me.user.email}</p>
          </div>
        </div>
        <div className="mt-3 flex items-center justify-between">
          {me.activeRole && <Badge>{roleLabels[me.activeRole]}</Badge>}
          <div className="flex items-center gap-1">
            {me.organizations.length > 1 && (
              <Link href="/select-organization" aria-label="Cambiar de organización" title="Cambiar de organización"
                className="flex size-8 items-center justify-center rounded-md text-ink-soft hover:bg-leaf hover:text-ink"><ArrowLeftRight size={16} /></Link>
            )}
            <button onClick={logout} aria-label="Cerrar sesión" title="Cerrar sesión"
              className="flex size-8 items-center justify-center rounded-md text-ink-soft hover:bg-leaf hover:text-ink"><LogOut size={16} /></button>
          </div>
        </div>
      </div>
    </div>
  );

  return (
    <MeContext.Provider value={me}>
      <NotificationsProvider>
      <div className="min-h-screen lg:pl-[248px]">
        <aside className="fixed inset-y-0 left-0 z-30 hidden w-[248px] border-r border-rule bg-paper-deep lg:block">{rail}</aside>

        <header className="flex h-14 items-center justify-between border-b border-rule bg-paper-deep px-4 lg:hidden">
          <Wordmark />
          <div className="flex items-center gap-1">
            <NotificationBell />
            <button onClick={() => setOpen(true)} aria-label="Abrir menú" className="flex size-9 items-center justify-center rounded-md hover:bg-leaf"><Menu size={18} /></button>
          </div>
        </header>
        {open && (
          <div className="fixed inset-0 z-40 lg:hidden">
            <button aria-label="Cerrar menú" onClick={() => setOpen(false)} className="absolute inset-0 bg-ink/40" />
            <aside className="relative h-full w-[280px] max-w-[85%] border-r border-rule bg-paper-deep">
              <button onClick={() => setOpen(false)} aria-label="Cerrar menú" className="absolute right-3 top-4 flex size-8 items-center justify-center rounded-md hover:bg-leaf"><X size={16} /></button>
              {rail}
            </aside>
          </div>
        )}

        <main className={cx("mx-auto w-full max-w-[960px] px-4 py-8 sm:px-8 sm:py-10", isTenant(role) && "pb-28 lg:pb-10")}>{children}</main>
        {isTenant(role) && (
          <nav aria-label="Portal" className="fixed inset-x-0 bottom-0 z-30 grid grid-cols-5 border-t border-rule bg-paper-deep pb-[env(safe-area-inset-bottom)] lg:hidden">
            {nav.filter((n) => n.visible(role)).map((n) => (
              <Link key={n.href} href={n.href} aria-current={isActive(pathname, n.href) ? "page" : undefined}
                className={cx("flex min-h-14 flex-col items-center justify-center gap-0.5 px-1 text-[11px] font-medium", isActive(pathname, n.href) ? "text-accent" : "text-ink-soft")}>
                <n.icon size={19} aria-hidden /> <span className="text-center leading-tight">{n.label}</span>
              </Link>
            ))}
          </nav>
        )}
      </div>
      </NotificationsProvider>
    </MeContext.Provider>
  );
}
