"use client";
import { Copy, KeyRound, MessageCircle } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import { Badge, Button, ErrorMessage, Leaf, Select, Skeleton, type Tone } from "@/components/ui";
import { api, roleLabels, type Paged, type Role, type Tenant } from "@/lib/api";

type AccessCode = { id: string; role: Role; tenantId: string | null; tenantName: string | null; status: "Pending" | "Used" | "Revoked" | "Expired"; expiresAt: string; createdAt: string };
type Created = { id: string; code: string; role: Role; tenantId: string | null; expiresAt: string };

const statusLabel: Record<AccessCode["status"], [string, Tone]> = {
  Pending: ["Pendiente", "pending"], Used: ["Usado", "ledger"], Revoked: ["Revocado", "neutral"], Expired: ["Vencido", "neutral"],
};

/** Owner/Admin hand out short single-use codes for collectors and tenants. The plain code is shown once, right after creating it. */
export function AccessCodes() {
  const [codes, setCodes] = useState<AccessCode[] | null>(null);
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [role, setRole] = useState<"Collector" | "Tenant">("Collector");
  const [created, setCreated] = useState<Created | null>(null);
  const [copied, setCopied] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(() => { api<AccessCode[]>("/access-codes").then(setCodes).catch((e) => setError(e.message)); }, []);
  useEffect(load, [load]);
  useEffect(() => {
    api<Paged<Tenant>>("/tenants?status=Active&pageSize=100").then((p) => setTenants(p.items)).catch(() => setTenants([]));
  }, []);

  async function create(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    setBusy(true); setError(null); setCreated(null); setCopied(false);
    try {
      const c = await api<Created>("/access-codes", { body: { role, tenantId: role === "Tenant" ? f.get("tenantId") : null, validDays: Number(f.get("validDays")) } });
      setCreated(c); load();
    } catch (err) { setError(err instanceof Error ? err.message : "No se pudo crear el código."); }
    finally { setBusy(false); }
  }

  async function revoke(id: string) {
    setError(null);
    try { await api(`/access-codes/${id}/revoke`, { method: "POST" }); load(); }
    catch (err) { setError(err instanceof Error ? err.message : "No se pudo revocar."); }
  }

  const link = created ? `${window.location.origin}/join?code=${created.code}` : "";
  const message = created ? `Tu código para entrar a NethRent: ${created.code}\n${link}` : "";

  return (
    <Leaf>
      <header className="px-5 pb-4 pt-5">
        <h2 className="text-[15px] font-semibold">Códigos de acceso</h2>
        <p className="mt-0.5 text-sm text-ink-soft">Un código corto y de un solo uso para un cobrador o un inquilino. Compártelo por mensaje.</p>
      </header>
      <div className="perforation" />
      <form onSubmit={create} className="grid items-end gap-4 px-5 pb-4 pt-4 sm:grid-cols-[170px_minmax(0,1fr)_120px_auto]">
        <Select label="Para" value={role} onChange={(e) => setRole(e.target.value as "Collector" | "Tenant")}>
          <option value="Collector">{roleLabels.Collector}</option>
          <option value="Tenant">{roleLabels.Tenant}</option>
        </Select>
        {role === "Tenant"
          ? <Select label="Inquilino" name="tenantId" required defaultValue="">
              <option value="" disabled>Elige un inquilino</option>
              {tenants.map((t) => <option key={t.id} value={t.id}>{t.fullName}</option>)}
            </Select>
          : <p className="pb-2 text-sm text-ink-soft">Registra pagos y ve inquilinos y cargos. Nada de contratos ni reportes.</p>}
        <Select label="Vigencia" name="validDays" defaultValue="7">
          <option value="1">1 día</option><option value="7">7 días</option><option value="30">30 días</option>
        </Select>
        <Button type="submit" loading={busy}><KeyRound size={15} aria-hidden /> Crear código</Button>
      </form>

      <div className="px-5 pb-4"><ErrorMessage message={error} /></div>

      {created && (
        <div role="status" className="mx-5 mb-5 rounded-lg border border-rule bg-paper p-4">
          <p className="text-sm text-ink-soft">Cópialo ahora: por seguridad no se vuelve a mostrar.</p>
          <p className="mt-2 font-mono text-2xl font-semibold tracking-widest">{created.code}</p>
          <div className="mt-3 flex flex-wrap gap-2">
            <Button variant="secondary" size="sm" onClick={async () => { await navigator.clipboard.writeText(message); setCopied(true); }}>
              <Copy size={14} aria-hidden /> {copied ? "Copiado" : "Copiar mensaje"}
            </Button>
            <a href={`https://wa.me/?text=${encodeURIComponent(message)}`} target="_blank" rel="noopener noreferrer"
              className="inline-flex h-8 items-center gap-1.5 rounded-md border border-rule bg-leaf px-3 text-sm font-medium hover:bg-paper">
              <MessageCircle size={14} aria-hidden /> Enviar por WhatsApp
            </a>
          </div>
        </div>
      )}

      {!codes && !error && <div className="flex flex-col gap-3 px-5 pb-5">{[0, 1].map((i) => <Skeleton key={i} className="h-8" />)}</div>}
      {codes && codes.length === 0 && <p className="px-5 pb-5 text-sm text-ink-soft">Aún no has creado códigos.</p>}
      {codes && codes.length > 0 && (
        <ul className="border-t border-rule">
          {codes.map((c) => {
            const [label, tone] = statusLabel[c.status];
            return (
              <li key={c.id} className="flex flex-wrap items-center justify-between gap-3 border-b border-rule px-5 py-3 last:border-b-0">
                <div className="min-w-0">
                  <p className="truncate text-sm font-medium">{roleLabels[c.role]}{c.tenantName ? ` · ${c.tenantName}` : ""}</p>
                  <p className="text-xs text-ink-faint">Vence {new Date(c.expiresAt).toLocaleDateString("es-MX", { day: "numeric", month: "short" })}</p>
                </div>
                <div className="flex items-center gap-3">
                  <Badge tone={tone}>{label}</Badge>
                  {c.status === "Pending" && <Button variant="secondary" size="sm" onClick={() => revoke(c.id)}>Revocar</Button>}
                </div>
              </li>
            );
          })}
        </ul>
      )}
    </Leaf>
  );
}
