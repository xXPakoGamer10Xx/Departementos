import { api, type PushConfig } from "@/lib/api";

/** The browser can do push at all (it also needs the service worker, which is only registered in production builds). */
export const pushSupported = () =>
  typeof window !== "undefined" && "serviceWorker" in navigator && "PushManager" in window && "Notification" in window;

function toKey(base64Url: string): Uint8Array<ArrayBuffer> {
  const padded = base64Url.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(base64Url.length / 4) * 4, "=");
  const raw = atob(padded);
  const out = new Uint8Array(new ArrayBuffer(raw.length));
  for (let i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i);
  return out;
}

async function registration() {
  return pushSupported() ? await navigator.serviceWorker.getRegistration() : undefined;
}

/** Is this device already subscribed? */
export async function currentSubscription(): Promise<PushSubscription | null> {
  const reg = await registration();
  return reg ? reg.pushManager.getSubscription() : null;
}

/** Asks permission, subscribes this device and registers it on the server. Throws an Error with a message fit for the person. */
export async function enablePush(): Promise<void> {
  if (!pushSupported()) throw new Error("Este navegador no admite avisos en el dispositivo. En iPhone, instala primero NethRent en tu pantalla de inicio.");
  const reg = await registration();
  if (!reg) throw new Error("Los avisos en el dispositivo solo funcionan en la versión instalada de NethRent.");
  const config = await api<PushConfig>("/notifications/push/config");
  if (!config.enabled || !config.publicKey) throw new Error("Este servidor no tiene activados los avisos en el dispositivo.");
  if ((await Notification.requestPermission()) !== "granted") throw new Error("No diste permiso para mostrar avisos. Puedes activarlo en los ajustes del navegador.");
  const sub = (await reg.pushManager.getSubscription()) ?? (await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: toKey(config.publicKey) }));
  const json = sub.toJSON();
  await api("/notifications/push/subscriptions", { body: { endpoint: json.endpoint, keys: { p256dh: json.keys?.p256dh, auth: json.keys?.auth } } });
}

export async function disablePush(): Promise<void> {
  const sub = await currentSubscription();
  if (!sub) return;
  const endpoint = sub.endpoint;
  await sub.unsubscribe();
  await api("/notifications/push/unsubscribe", { body: { endpoint } }).catch(() => {}); // already forgotten by the server is fine
}
