"use client";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { AuthCard, Button, ErrorMessage, Field } from "@/components/ui";
import { api } from "@/lib/api";

export default function RegisterPage() {
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  async function onSubmit(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    setLoading(true); setError(null);
    try {
      await api("/auth/register", {
        body: { fullName: f.get("fullName"), organizationName: f.get("organizationName"), email: f.get("email"), password: f.get("password") },
      });
      router.push("/dashboard");
    } catch (err) {
      setError(err instanceof Error ? err.message : "No se pudo crear la cuenta.");
    } finally { setLoading(false); }
  }

  return (
    <AuthCard
      title="Crear organización"
      subtitle="Serás el propietario y podrás invitar a tu equipo después."
      footer={<span>¿Ya tienes cuenta? <Link href="/login" className="font-medium text-accent hover:underline">Iniciar sesión</Link></span>}
    >
      <form onSubmit={onSubmit} className="flex flex-col gap-5">
        <Field label="Tu nombre" name="fullName" autoComplete="name" required />
        <Field label="Nombre de la organización" name="organizationName" required />
        <Field label="Correo electrónico" name="email" type="email" autoComplete="email" required />
        <Field label="Contraseña" name="password" type="password" autoComplete="new-password" minLength={10} hint="Mínimo 10 caracteres, con mayúscula, minúscula, número y símbolo." required />
        <ErrorMessage message={error} />
        <Button type="submit" loading={loading}>Crear cuenta</Button>
      </form>
    </AuthCard>
  );
}
