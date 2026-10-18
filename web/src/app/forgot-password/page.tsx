"use client";
import Link from "next/link";
import { useState } from "react";
import { AuthCard, Button, ErrorMessage, Field, SuccessMessage } from "@/components/ui";
import { api } from "@/lib/api";

export default function ForgotPasswordPage() {
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  async function onSubmit(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    setLoading(true); setError(null);
    try {
      await api("/auth/forgot-password", { body: { email: new FormData(e.currentTarget).get("email") } });
      setDone("Si la cuenta existe, te enviamos un correo con instrucciones.");
    } catch (err) {
      setError(err instanceof Error ? err.message : "No se pudo enviar el correo.");
    } finally { setLoading(false); }
  }

  return (
    <AuthCard
      title="Recuperar contraseña"
      subtitle="Te enviaremos un enlace para crear una contraseña nueva."
      footer={<Link href="/login" className="font-medium text-accent hover:underline">Volver a iniciar sesión</Link>}
    >
      <form onSubmit={onSubmit} className="flex flex-col gap-5">
        <Field label="Correo electrónico" name="email" type="email" autoComplete="email" required />
        <ErrorMessage message={error} />
        <SuccessMessage message={done} />
        <Button type="submit" loading={loading}>Enviar enlace</Button>
      </form>
    </AuthCard>
  );
}
