"use client";
import { useEffect, useEffectEvent, useState } from "react";

/**
 * Fetches when `key` changes. Keeps the previous data visible while a new key loads (no skeleton flicker on search),
 * and reports `loading` while the visible data belongs to an older key.
 */
export function useQuery<T>(key: string, fetcher: () => Promise<T>) {
  const [state, setState] = useState<{ key: string; data?: T; error?: { message: string; status?: number } }>({ key: "" });
  const run = useEffectEvent(fetcher);

  useEffect(() => {
    let cancelled = false;
    run().then(
      (data) => { if (!cancelled) setState({ key, data }); },
      (e: { message: string; status?: number }) => { if (!cancelled) setState({ key, error: { message: e.message, status: e.status } }); },
    );
    return () => { cancelled = true; };
  }, [key]);

  return { data: state.data, error: state.key === key ? state.error : undefined, loading: state.key !== key };
}
