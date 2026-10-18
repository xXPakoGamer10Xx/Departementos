"use client";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { AuthCard, Button, ErrorMessage, Field, SuccessMessage } from "@/components/ui";
import { api } from "@/lib/api";

function ResetForm() {
  const params = useSearchParams();
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  async function onSubmit(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    setLoading(true); setError(null);
    try {
      await api("/auth/reset-password", {
        body: { email: params.get("email"), token: params.get("token"), newPassword: new FormData(e.currentTarget).get("password") },
      });
      setDone("Contraseña actualizada. Ya puedes iniciar sesión.");
    } catch (err) {
      setError(err instanceof Error ? err.message : "No se pudo restablecer la contraseña.");
    } finally { setLoading(false); }
  }

  return (
    <AuthCard
      title="Nueva contraseña"
      subtitle="Elige una contraseña que no uses en otro sitio."
      footer={<Link href="/login" className="font-medium text-accent hover:underline">Ir a iniciar sesión</Link>}
    >
      <form onSubmit={onSubmit} className="flex flex-col gap-5">
        <Field label="Nueva contraseña" name="password" type="password" autoComplete="new-password" minLength={10} hint="Mínimo 10 caracteres, con mayúscula, minúscula, número y símbolo." required />
        <ErrorMessage message={error} />
        <SuccessMessage message={done} />
        <Button type="submit" loading={loading}>Guardar contraseña</Button>
      </form>
    </AuthCard>
  );
}

export default function ResetPasswordPage() {
  return <Suspense fallback={null}><ResetForm /></Suspense>;
}
