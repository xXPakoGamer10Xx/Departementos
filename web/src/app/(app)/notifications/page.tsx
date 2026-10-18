"use client";
import { BellOff, BellRing, Mail, Smartphone } from "lucide-react";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { NotificationRow, useNotifications } from "@/components/notifications";
import { Button, EmptyState, ErrorMessage, Leaf, Pager, Skeleton, SuccessMessage } from "@/components/ui";
import { api, type AppNotification, type NotificationList, type NotificationPrefs, type PushConfig } from "@/lib/api";
import { currentSubscription, disablePush, enablePush, pushSupported } from "@/lib/push";
import { useQuery } from "@/lib/use-query";

const PAGE_SIZE = 15;

export default function NotificationsPage() {
  const router = useRouter();
  const live = useNotifications();
  const [page, setPage] = useState(1);
  const [unreadOnly, setUnreadOnly] = useState(false);
  const [version, setVersion] = useState(0);
  const { data, error } = useQuery(`notifications:${page}:${unreadOnly}:${version}:${live.unread}`, () =>
    api<NotificationList>(`/notifications?page=${page}&pageSize=${PAGE_SIZE}${unreadOnly ? "&unreadOnly=true" : ""}`));

  async function open(n: AppNotification) {
    await live.markRead(n.id);
    router.push(n.url);
  }

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Avisos</h1>
          <p className="mt-1 text-sm text-ink-soft">Lo que pasa en tu cuenta. Cada aviso empieza por el nombre de la persona de la que habla.</p>
        </div>
        <div className="flex gap-2">
          <Button size="sm" variant={unreadOnly ? "primary" : "secondary"} onClick={() => { setUnreadOnly((v) => !v); setPage(1); }}>Solo no leídos</Button>
          <Button size="sm" variant="secondary" disabled={live.unread === 0} onClick={async () => { await live.markAllRead(); setVersion((v) => v + 1); }}>Marcar todo como leído</Button>
        </div>
      </div>
      <ErrorMessage message={error?.message ?? null} />
      <Leaf className="overflow-hidden">
        {!data && !error && <div className="flex flex-col gap-3 p-5">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-14" />)}</div>}
        {data?.items.length === 0 && <EmptyState icon={<BellOff size={18} />} title={unreadOnly ? "No tienes avisos sin leer" : "Aún no tienes avisos"}>Cuando pase algo que te importe, aparecerá aquí.</EmptyState>}
        {data && data.items.length > 0 && <ul>{data.items.map((n) => <NotificationRow key={n.id} n={n} onOpen={open} />)}</ul>}
        {data && <Pager page={page} pageSize={PAGE_SIZE} total={data.total} onPage={setPage} />}
      </Leaf>
      <Preferences />
    </div>
  );
}

function Toggle({ label, hint, icon, checked, disabled, onChange }: { label: string; hint: string; icon: React.ReactNode; checked: boolean; disabled?: boolean; onChange: (v: boolean) => void }) {
  return (
    <label className="flex cursor-pointer items-start gap-3 px-5 py-4">
      <span className="mt-0.5 text-accent" aria-hidden>{icon}</span>
      <span className="min-w-0 flex-1"><span className="block text-sm font-medium">{label}</span><span className="block text-xs text-ink-soft">{hint}</span></span>
      <input type="checkbox" checked={checked} disabled={disabled} onChange={(e) => onChange(e.target.checked)} className="mt-1 size-4 accent-[var(--color-accent)]" />
    </label>
  );
}

function Preferences() {
  const [version, setVersion] = useState(0);
  const prefs = useQuery(`prefs:${version}`, () => api<NotificationPrefs>("/notifications/preferences"));
  const push = useQuery("push:config", () => api<PushConfig>("/notifications/push/config"));
  const [subscribed, setSubscribed] = useState<boolean | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  useEffect(() => { currentSubscription().then((s) => setSubscribed(!!s), () => setSubscribed(false)); }, []);

  async function save(change: Partial<NotificationPrefs>) {
    setError(null); setNotice(null);
    try { await api("/notifications/preferences", { method: "PUT", body: change }); setVersion((v) => v + 1); }
    catch (e) { setError(e instanceof Error ? e.message : "No se pudo guardar."); }
  }

  async function toggleDevice() {
    setBusy(true); setError(null); setNotice(null);
    try {
      if (subscribed) { await disablePush(); setSubscribed(false); setNotice("Este dispositivo ya no recibirá avisos."); }
      else { await enablePush(); setSubscribed(true); setNotice("Listo: este dispositivo recibirá avisos."); }
    } catch (e) { setError(e instanceof Error ? e.message : "No se pudo cambiar."); }
    finally { setBusy(false); }
  }

  return (
    <Leaf>
      <header className="px-5 pb-4 pt-5"><h2 className="text-[15px] font-semibold">Qué recibir fuera de la app</h2><p className="mt-0.5 text-sm text-ink-soft">Los avisos siempre aparecen aquí. Elige si además te llegan por correo o al dispositivo.</p></header>
      <div className="perforation" />
      {!prefs.data && !prefs.error && <div className="p-5"><Skeleton className="h-12" /></div>}
      {prefs.data && (
        <div className="divide-y divide-rule">
          <Toggle label="Correo" hint="Te enviamos cada aviso a tu correo de acceso." icon={<Mail size={18} />} checked={prefs.data.emailEnabled} onChange={(v) => save({ emailEnabled: v })} />
          <Toggle label="Avisos en el dispositivo" hint="Una notificación en el teléfono o la computadora, con solo el título." icon={<Smartphone size={18} />} checked={prefs.data.pushEnabled} onChange={(v) => save({ pushEnabled: v })} />
        </div>
      )}
      <div className="perforation" />
      <div className="flex flex-col gap-3 px-5 py-4">
        <SuccessMessage message={notice} />
        <ErrorMessage message={error ?? prefs.error?.message ?? null} />
        {push.data && !push.data.enabled && <p className="text-sm text-ink-soft">Este servidor aún no tiene activados los avisos en el dispositivo.</p>}
        {push.data?.enabled && !pushSupported() && <p className="text-sm text-ink-soft">Este navegador no admite avisos en el dispositivo. En iPhone, instala primero Habita en tu pantalla de inicio (Compartir → Añadir a pantalla de inicio).</p>}
        {push.data?.enabled && pushSupported() && (
          <div className="flex flex-wrap items-center justify-between gap-3">
            <p className="text-sm text-ink-soft">{subscribed ? "Este dispositivo recibe avisos." : "Este dispositivo no recibe avisos."}</p>
            <Button variant={subscribed ? "secondary" : "primary"} loading={busy} disabled={subscribed === null} onClick={toggleDevice}><BellRing size={15} aria-hidden /> {subscribed ? "Dejar de recibir aquí" : "Activar en este dispositivo"}</Button>
          </div>
        )}
      </div>
    </Leaf>
  );
}
