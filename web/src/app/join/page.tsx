"use client";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useEffect, useState } from "react";
import { AuthCard, Button, ErrorMessage, Field } from "@/components/ui";
import { ApiError, api, homePath, type Me } from "@/lib/api";

function JoinForm() {
  const router = useRouter();
  const initial = useSearchParams().get("code") ?? "";
  const [signedIn, setSignedIn] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [needsLogin, setNeedsLogin] = useState(false);
  const [loading, setLoading] = useState(false);

  // A person who already has an account just adds the code; a new one also creates an account.
  useEffect(() => { api<Me>("/me").then((m) => setSignedIn(m.user.email)).catch(() => setSignedIn(null)); }, []);

  async function onSubmit(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    setLoading(true); setError(null); setNeedsLogin(false);
    try {
      await api("/auth/access-codes/redeem", {
        body: { code: f.get("code"), email: f.get("email") || null, fullName: f.get("fullName") || null, password: f.get("password") || null },
      });
      const me = await api<Me>("/me");
      router.push(homePath(me.activeRole));
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) setNeedsLogin(true);
      setError(err instanceof Error ? err.message : "No se pudo usar el código.");
    } finally { setLoading(false); }
  }

  return (
    <AuthCard
      title="Entrar con un código"
      subtitle="Te lo compartió quien administra la propiedad. Sirve una sola vez."
      footer={<Link href="/login" className="font-medium text-accent hover:underline">{needsLogin ? "Iniciar sesión y volver" : "Ya tengo cuenta"}</Link>}
    >
      <form onSubmit={onSubmit} className="flex flex-col gap-5">
        <Field label="Código" name="code" defaultValue={initial} required autoComplete="off" autoCapitalize="characters" spellCheck={false}
          className="font-mono uppercase tracking-widest" placeholder="XXXX-XXXX-XX" />
        {signedIn ? (
          <p className="text-sm text-ink-soft">Entrarás con tu cuenta <strong className="font-medium text-ink">{signedIn}</strong>.</p>
        ) : (
          <>
            <Field label="Tu nombre" name="fullName" autoComplete="name" required />
            <Field label="Correo electrónico" name="email" type="email" autoComplete="email" required />
            <Field label="Contraseña" name="password" type="password" autoComplete="new-password" minLength={10} required hint="Mínimo 10 caracteres." />
          </>
        )}
        <ErrorMessage message={error} />
        <Button type="submit" loading={loading}>Continuar</Button>
      </form>
    </AuthCard>
  );
}

export default function JoinPage() {
  return <Suspense fallback={null}><JoinForm /></Suspense>;
}
