"use client";
import { useEffect } from "react";

/** Registers the service worker (installability + offline page) in production only; in dev it would fight hot reload. */
export function RegisterServiceWorker() {
  useEffect(() => {
    if (process.env.NODE_ENV === "production" && "serviceWorker" in navigator) {
      navigator.serviceWorker.register("/sw.js", { scope: "/" }).catch(() => {});
    }
  }, []);
  return null;
}
