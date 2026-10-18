"use client";
import { Bell, BellRing, CheckCheck } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { cx } from "@/components/ui";
import { api, timeAgo, type AppNotification, type NotificationList } from "@/lib/api";

type State = {
  items: AppNotification[]; unread: number; loaded: boolean;
  markRead: (id: string) => Promise<void>; markAllRead: () => Promise<void>; reload: () => Promise<void>;
};
const Ctx = createContext<State | null>(null);
const KEEP = 50;

export function useNotifications(): State {
  const v = useContext(Ctx);
  if (!v) throw new Error("useNotifications debe usarse dentro de NotificationsProvider.");
  return v;
}

/**
 * Loads the person's latest notifications and keeps them current through a live stream (Server-Sent Events). The stream ends on its own every few
 * minutes and the browser reconnects, so a new connection re-sends the last minute: ids already shown are ignored, never counted twice.
 */
export function NotificationsProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<AppNotification[]>([]);
  const [unread, setUnread] = useState(0);
  const [loaded, setLoaded] = useState(false);
  const seen = useRef(new Set<string>());

  const reload = useCallback(async () => {
    try {
      const list = await api<NotificationList>("/notifications?pageSize=20");
      seen.current = new Set(list.items.map((n) => n.id));
      setItems(list.items); setUnread(list.unread);
    } catch { /* the bell simply stays as it was */ }
    setLoaded(true);
  }, []);

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- the initial load is the whole point of this effect
    reload();
    const source = new EventSource("/api/v1/notifications/stream", { withCredentials: true });
    source.addEventListener("notification", (e) => {
      const n = JSON.parse((e as MessageEvent<string>).data) as AppNotification;
      if (seen.current.has(n.id)) return;
      seen.current.add(n.id);
      setItems((prev) => [n, ...prev].slice(0, KEEP));
      if (!n.read) setUnread((u) => u + 1);
    });
    return () => source.close();
  }, [reload]);

  const markRead = useCallback(async (id: string) => {
    let wasUnread = false;
    setItems((prev) => prev.map((n) => (n.id === id && !n.read ? ((wasUnread = true), { ...n, read: true }) : n)));
    if (wasUnread) setUnread((u) => Math.max(0, u - 1));
    await api(`/notifications/${id}/read`, { method: "POST" }).catch(() => {});
  }, []);

  const markAllRead = useCallback(async () => {
    setItems((prev) => prev.map((n) => ({ ...n, read: true }))); setUnread(0);
    await api("/notifications/read-all", { method: "POST" }).catch(() => {});
  }, []);

  const value = useMemo(() => ({ items, unread, loaded, markRead, markAllRead, reload }), [items, unread, loaded, markRead, markAllRead, reload]);
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

/** One notification as a row. The title already starts with the person's name; the body adds the detail. */
export function NotificationRow({ n, onOpen, compact }: { n: AppNotification; onOpen: (n: AppNotification) => void; compact?: boolean }) {
  return (
    <li>
      <button type="button" onClick={() => onOpen(n)} className={cx("flex w-full items-start gap-3 border-b border-rule px-4 py-3 text-left last:border-b-0 hover:bg-paper", !n.read && "bg-accent-soft/40")}>
        <span aria-hidden className={cx("mt-1.5 size-2 shrink-0 rounded-full", n.read ? "bg-transparent" : "bg-accent")} />
        <span className="min-w-0 flex-1">
          <span className={cx("block text-sm", n.read ? "font-medium text-ink-soft" : "font-semibold")}>{n.title}</span>
          {n.body && <span className={cx("mt-0.5 block text-xs text-ink-soft", compact && "line-clamp-2")}>{n.body}</span>}
          <span className="mt-1 block text-xs text-ink-faint">{timeAgo(n.createdAt)}</span>
        </span>
        {!n.read && <span className="sr-only">No leída</span>}
      </button>
    </li>
  );
}

/** The bell with the unread count and a short list. Opens a panel; Escape or a click outside closes it. */
export function NotificationBell({ className }: { className?: string }) {
  const { items, unread, markRead, markAllRead } = useNotifications();
  const router = useRouter();
  const [open, setOpen] = useState(false);
  const box = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") setOpen(false); };
    const onClick = (e: MouseEvent) => { if (box.current && !box.current.contains(e.target as Node)) setOpen(false); };
    document.addEventListener("keydown", onKey); document.addEventListener("mousedown", onClick);
    return () => { document.removeEventListener("keydown", onKey); document.removeEventListener("mousedown", onClick); };
  }, [open]);

  function openItem(n: AppNotification) {
    setOpen(false);
    void markRead(n.id);
    router.push(n.url);
  }

  const Icon = unread > 0 ? BellRing : Bell;
  return (
    <div ref={box} className={cx("relative", className)}>
      <button type="button" onClick={() => setOpen((v) => !v)} aria-expanded={open} aria-haspopup="dialog"
        aria-label={unread > 0 ? `Avisos: ${unread} sin leer` : "Avisos"} title="Avisos"
        className="relative flex size-9 items-center justify-center rounded-md text-ink-soft hover:bg-leaf hover:text-ink">
        <Icon size={18} aria-hidden />
        {unread > 0 && <span aria-hidden className="absolute -right-0.5 -top-0.5 flex min-w-[18px] items-center justify-center rounded-full bg-stamp px-1 text-[11px] font-semibold leading-[18px] text-white">{unread > 99 ? "99+" : unread}</span>}
      </button>
      {open && (
        <div role="dialog" aria-label="Avisos" className="fixed inset-x-2 top-14 z-50 rounded-lg border border-rule-strong bg-leaf shadow-lg sm:absolute sm:inset-x-auto sm:left-0 sm:top-11 sm:w-[380px]">
          <header className="flex items-center justify-between border-b border-rule px-4 py-3">
            <h2 className="text-sm font-semibold">Avisos</h2>
            {unread > 0 && <button type="button" onClick={() => void markAllRead()} className="inline-flex items-center gap-1 text-xs font-medium text-accent hover:underline"><CheckCheck size={14} aria-hidden /> Marcar todo como leído</button>}
          </header>
          {items.length === 0
            ? <p className="px-4 py-8 text-center text-sm text-ink-soft">No tienes avisos todavía.</p>
            : <ul className="max-h-[60vh] overflow-auto">{items.slice(0, 8).map((n) => <NotificationRow key={n.id} n={n} onOpen={openItem} compact />)}</ul>}
          <footer className="border-t border-rule px-4 py-2.5 text-right"><Link href="/notifications" onClick={() => setOpen(false)} className="text-sm font-medium text-accent hover:underline">Ver todos y elegir qué recibir</Link></footer>
        </div>
      )}
    </div>
  );
}
