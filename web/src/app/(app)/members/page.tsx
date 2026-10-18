"use client";
import { Lock, UserPlus, Users } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import { useMe } from "@/components/me-context";
import { Avatar, Badge, Button, EmptyState, ErrorMessage, Field, Leaf, Select, Skeleton, SuccessMessage } from "@/components/ui";
import { AccessCodes } from "@/components/access-codes";
import { ApiError, api, assignableRoles, roleLabels, type Member, type Role } from "@/lib/api";

const roles = assignableRoles;
const cols = "md:grid-cols-[minmax(0,1fr)_190px_110px_110px]";

export default function MembersPage() {
  const me = useMe();
  const [members, setMembers] = useState<Member[] | null>(null);
  const [forbidden, setForbidden] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [inviting, setInviting] = useState(false);

  const load = useCallback(() => {
    api<Member[]>("/members").then(setMembers).catch((e) => {
      if (e instanceof ApiError && e.status === 403) setForbidden(true); else setError(e.message);
    });
  }, []);
  useEffect(load, [load]);

  async function invite(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const form = e.currentTarget;
    const f = new FormData(form);
    setInviting(true); setError(null); setNotice(null);
    try {
      await api("/members/invitations", { body: { email: f.get("email"), role: f.get("role") } });
      setNotice(`Invitación enviada a ${f.get("email")}. El enlace vence en 7 días.`);
      form.reset();
    } catch (err) { setError(err instanceof Error ? err.message : "No se pudo invitar."); }
    finally { setInviting(false); }
  }

  async function update(id: string, body: { role?: Role; status?: "Active" | "Deactivated" }) {
    setError(null); setNotice(null);
    try { await api(`/members/${id}`, { method: "PATCH", body }); load(); }
    catch (err) { setError(err instanceof Error ? err.message : "No se pudo actualizar."); }
  }

  if (forbidden) {
    return (
      <Leaf><EmptyState icon={<Lock size={18} />} title="No tienes permiso para ver esta sección">
        Solo los propietarios y administradores pueden gestionar miembros.
      </EmptyState></Leaf>
    );
  }

  return (
    <div className="flex flex-col gap-6">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Miembros</h1>
        <p className="mt-1 text-sm text-ink-soft">Quién tiene acceso a esta organización y con qué rol.</p>
      </div>

      <ErrorMessage message={error} />
      <SuccessMessage message={notice} />

      <Leaf>
        <header className="px-5 pb-4 pt-5">
          <h2 className="text-[15px] font-semibold">Invitar a una persona</h2>
          <p className="mt-0.5 text-sm text-ink-soft">Recibirá un correo con un enlace para unirse.</p>
        </header>
        <div className="perforation" />
        <form onSubmit={invite} className="grid items-end gap-4 px-5 pb-5 pt-4 sm:grid-cols-[minmax(0,1fr)_190px_auto]">
          <Field label="Correo electrónico" name="email" type="email" required />
          <Select label="Rol" name="role" defaultValue="ReadOnly">
            {roles.map((r) => <option key={r} value={r}>{roleLabels[r]}</option>)}
          </Select>
          <Button type="submit" loading={inviting}><UserPlus size={15} aria-hidden /> Invitar</Button>
        </form>
      </Leaf>

      <Leaf className="overflow-hidden">
        <div aria-hidden className={`hidden gap-4 border-b border-rule bg-paper px-5 py-2.5 text-xs font-medium text-ink-soft md:grid ${cols}`}>
          <span>Persona</span><span>Rol</span><span>Estado</span><span />
        </div>
        {!members && !error && (
          <div className="flex flex-col gap-4 p-5">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-9" />)}</div>
        )}
        {members?.length === 0 && (
          <EmptyState icon={<Users size={18} />} title="Aún no hay miembros">Invita a tu equipo con el formulario de arriba.</EmptyState>
        )}
        {members && members.length > 0 && (
          <ul>
            {members.map((m) => {
              const mine = m.userId === me.user.id;
              const active = m.status === "Active";
              return (
                <li key={m.id} className={`grid items-center gap-x-4 gap-y-3 border-b border-rule px-5 py-3.5 last:border-b-0 ${cols}`}>
                  <div className="flex min-w-0 items-center gap-3">
                    <Avatar name={m.fullName ?? m.email ?? "?"} />
                    <div className="min-w-0">
                      <p className="truncate text-sm font-medium">{m.fullName}{mine && <span className="ml-2 text-xs font-normal text-ink-faint">(tú)</span>}</p>
                      <p className="truncate text-xs text-ink-faint">{m.email}</p>
                    </div>
                  </div>
                  {m.role === "Tenant" ? (
                    <div><Badge>{roleLabels.Tenant}</Badge></div> // a tenant login is tied to its tenant record and never changes role
                  ) : (
                    <Select aria-label={`Rol de ${m.fullName ?? m.email}`} value={m.role} disabled={mine}
                      onChange={(e) => update(m.id, { role: e.target.value as Role })} className="h-9">
                      {roles.map((r) => <option key={r} value={r}>{roleLabels[r]}</option>)}
                    </Select>
                  )}
                  <div><Badge tone={active ? "ledger" : "neutral"}>{active ? "Activo" : "Desactivado"}</Badge></div>
                  <div className="md:text-right">
                    {!mine && (
                      <Button variant={active ? "danger" : "secondary"} size="sm"
                        onClick={() => update(m.id, { status: active ? "Deactivated" : "Active" })}>
                        {active ? "Desactivar" : "Reactivar"}
                      </Button>
                    )}
                  </div>
                </li>
              );
            })}
          </ul>
        )}
      </Leaf>

      <AccessCodes />
    </div>
  );
}
