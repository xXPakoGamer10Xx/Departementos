"use client";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { AuthCard, Button, ErrorMessage, Field } from "@/components/ui";
import { api, homePath, type Me } from "@/lib/api";

export default function LoginPage() {
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  async function onSubmit(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    setLoading(true); setError(null);
    try {
      await api("/auth/login", { body: { email: f.get("email"), password: f.get("password") } });
      const me = await api<Me>("/me");
      router.push(me.activeOrganizationId ? homePath(me.activeRole) : "/select-organization");
    } catch (err) {
      setError(err instanceof Error ? err.message : "No se pudo iniciar sesión.");
    } finally { setLoading(false); }
  }

  return (
    <AuthCard
      title="Iniciar sesión"
      subtitle="Entra a tu organización para administrar tus propiedades."
      footer={<><Link href="/register" className="font-medium text-accent hover:underline">Crear una organización</Link><Link href="/join" className="hover:text-ink hover:underline">Tengo un código</Link><Link href="/forgot-password" className="hover:text-ink hover:underline">Olvidé mi contraseña</Link></>}
    >
      <form onSubmit={onSubmit} className="flex flex-col gap-5">
        <Field label="Correo electrónico" name="email" type="email" autoComplete="email" required />
        <Field label="Contraseña" name="password" type="password" autoComplete="current-password" required />
        <ErrorMessage message={error} />
        <Button type="submit" loading={loading}>Entrar</Button>
      </form>
    </AuthCard>
  );
}
