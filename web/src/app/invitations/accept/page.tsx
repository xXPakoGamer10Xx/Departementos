"use client";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { AuthCard, Button, ErrorMessage, Field } from "@/components/ui";
import { ApiError, api, homePath, type Me } from "@/lib/api";

function AcceptForm() {
  const router = useRouter();
  const token = useSearchParams().get("token");
  const [error, setError] = useState<string | null>(null);
  const [needsLogin, setNeedsLogin] = useState(false);
  const [loading, setLoading] = useState(false);

  async function onSubmit(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    setLoading(true); setError(null);
    try {
      await api("/auth/invitations/accept", { body: { token, fullName: f.get("fullName") || null, password: f.get("password") || null } });
      const me = await api<Me>("/me");
      router.push(homePath(me.activeRole));
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) setNeedsLogin(true);
      setError(err instanceof Error ? err.message : "No se pudo aceptar la invitación.");
    } finally { setLoading(false); }
  }

  if (!token) {
    return (
      <AuthCard title="Invitación no válida" subtitle="El enlace está incompleto. Pide que te envíen la invitación de nuevo."
        footer={<Link href="/login" className="font-medium text-accent hover:underline">Ir a iniciar sesión</Link>}>
        <></>
      </AuthCard>
    );
  }
  return (
    <AuthCard
      title="Aceptar invitación"
      subtitle="Si ya tienes cuenta, inicia sesión y vuelve a este enlace. Si eres nuevo, completa tus datos."
      footer={needsLogin ? <Link href="/login" className="font-medium text-accent hover:underline">Iniciar sesión</Link> : undefined}
    >
      <form onSubmit={onSubmit} className="flex flex-col gap-5">
        <Field label="Tu nombre" name="fullName" autoComplete="name" hint="Solo para cuentas nuevas." />
        <Field label="Contraseña" name="password" type="password" autoComplete="new-password" minLength={10} hint="Solo para cuentas nuevas. Mínimo 10 caracteres." />
        <ErrorMessage message={error} />
        <Button type="submit" loading={loading}>Aceptar invitación</Button>
      </form>
    </AuthCard>
  );
}

export default function AcceptInvitationPage() {
  return <Suspense fallback={null}><AcceptForm /></Suspense>;
}
