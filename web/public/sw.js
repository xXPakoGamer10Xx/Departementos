// Habita service worker. It makes the app installable, shows a friendly page when the phone is offline and shows push notifications.
// It deliberately caches NOTHING else: no API responses, no pages with account data. A tenant's balance must never be
// served from a stale or shared cache, so every request goes to the network.
const OFFLINE = "/offline.html";
const CACHE = "habita-shell-v1";

self.addEventListener("install", (event) => {
  event.waitUntil(caches.open(CACHE).then((c) => c.addAll([OFFLINE, "/icon-192.png"])).then(() => self.skipWaiting()));
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches.keys().then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k)))).then(() => self.clients.claim()),
  );
});

self.addEventListener("fetch", (event) => {
  const req = event.request;
  if (req.mode !== "navigate") return; // everything else (API, scripts, images) is left to the browser
  event.respondWith(fetch(req).catch(() => caches.match(OFFLINE)));
});

// Push: the server sends only a title (which opens with the person's name), a generic line and an in-app path. Never amounts or account data.
self.addEventListener("push", (event) => {
  let data = {};
  try { data = event.data ? event.data.json() : {}; } catch { /* not JSON: show the generic notification */ }
  // The link must stay inside the app, whatever the payload says.
  const url = typeof data.url === "string" && data.url.startsWith("/") && !data.url.startsWith("//") ? data.url : "/";
  event.waitUntil(
    self.registration.showNotification(typeof data.title === "string" && data.title ? data.title : "Habita", {
      body: typeof data.body === "string" ? data.body : "",
      icon: "/icon-192.png",
      badge: "/icon-192.png",
      tag: typeof data.tag === "string" ? data.tag : undefined,
      data: { url },
    }),
  );
});

self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  const url = (event.notification.data && event.notification.data.url) || "/";
  event.waitUntil(
    self.clients.matchAll({ type: "window", includeUncontrolled: true }).then((windows) => {
      for (const w of windows) if ("focus" in w) return w.navigate(url).then(() => w.focus());
      return self.clients.openWindow(url);
    }),
  );
});
