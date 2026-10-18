"use client";
import { ChevronRight } from "lucide-react";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { AuthCard, Badge, ErrorMessage, Skeleton } from "@/components/ui";
import { api, homePath, roleLabels, type Me } from "@/lib/api";

export default function SelectOrganizationPage() {
  const router = useRouter();
  const [me, setMe] = useState<Me | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api<Me>("/me").then(setMe).catch((e) => {
      if (e?.status === 401) router.replace("/login"); else setError(e.message);
    });
  }, [router]);

  async function choose(id: string) {
    setError(null);
    try {
      await api("/me/active-organization", { body: { organizationId: id } });
      router.push(homePath(me?.organizations.find((o) => o.id === id)?.role ?? null));
    } catch (e) { setError(e instanceof Error ? e.message : "No se pudo cambiar de organización."); }
  }

  return (
    <AuthCard title="Elige una organización" subtitle="Puedes cambiar de organización en cualquier momento.">
      <ErrorMessage message={error} />
      {!me && !error && <div className="flex flex-col gap-2"><Skeleton className="h-14" /><Skeleton className="h-14" /></div>}
      {me?.organizations.length === 0 && <p className="text-sm text-ink-soft">No perteneces a ninguna organización activa.</p>}
      <ul className="flex flex-col gap-2">
        {me?.organizations.map((o) => (
          <li key={o.id}>
            <button onClick={() => choose(o.id)}
              className="group flex w-full items-center justify-between gap-3 rounded-md border border-rule-strong bg-leaf px-4 py-3 text-left transition-colors hover:border-accent hover:bg-accent-soft/40">
              <span className="min-w-0">
                <span className="block truncate font-medium">{o.name}</span>
                <span className="font-mono text-xs text-ink-faint">N.º {o.id.slice(0, 6).toUpperCase()}</span>
              </span>
              <span className="flex items-center gap-2"><Badge>{roleLabels[o.role]}</Badge><ChevronRight size={16} className="text-ink-faint transition-transform group-hover:translate-x-0.5" aria-hidden /></span>
            </button>
          </li>
        ))}
      </ul>
    </AuthCard>
  );
}
