"use client";
import { AlertCircle, CheckCircle2, ChevronDown, Loader2, Search, X } from "lucide-react";
import { useEffect, useId, useState, type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes, type TextareaHTMLAttributes } from "react";

export const cx = (...c: (string | false | null | undefined)[]) => c.filter(Boolean).join(" ");

/** Marca provisional: un arco de puerta, "habitar". Sustituir por el logotipo real cuando exista. */
export function BrandMark({ size = 28 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 28 28" aria-hidden="true">
      <rect width="28" height="28" rx="7" fill="var(--color-accent)" />
      <path d="M8.5 21V13.2a5.5 5.5 0 0 1 11 0V21" fill="none" stroke="#fff" strokeWidth="2.2" strokeLinecap="round" />
      <path d="M12.2 21v-4.3a1.8 1.8 0 0 1 3.6 0V21" fill="#fff" />
    </svg>
  );
}

export function Wordmark() {
  return (
    <span className="inline-flex items-center gap-2.5">
      <BrandMark />
      <span className="text-[17px] font-semibold tracking-tight">Habita</span>
    </span>
  );
}

/** Pantalla de autenticación: una hoja del talonario sobre el papel. */
export function AuthCard({ title, subtitle, children, footer }: { title: string; subtitle?: string; children: ReactNode; footer?: ReactNode }) {
  return (
    <main className="flex min-h-screen flex-col items-center justify-center gap-6 px-4 py-10">
      <Wordmark />
      <section className="w-full max-w-[400px] rounded-lg border border-rule bg-leaf">
        <header className="px-7 pb-6 pt-7">
          <h1 className="text-xl font-semibold tracking-tight">{title}</h1>
          {subtitle && <p className="mt-1.5 text-sm text-ink-soft">{subtitle}</p>}
        </header>
        <div className="perforation" />
        <div className="flex flex-col gap-5 px-7 pb-7 pt-6">{children}</div>
      </section>
      {footer && <div className="flex w-full max-w-[400px] items-center justify-between gap-4 text-sm text-ink-soft">{footer}</div>}
    </main>
  );
}

export function Leaf({ children, className }: { children: ReactNode; className?: string }) {
  return <section className={cx("rounded-lg border border-rule bg-leaf", className)}>{children}</section>;
}

type FieldProps = { label: string; hint?: string; error?: string | null } & InputHTMLAttributes<HTMLInputElement>;
const controlBase =
  "h-10 w-full rounded-md border border-rule-strong bg-leaf px-3 text-sm text-ink placeholder:text-ink-faint transition-colors hover:border-ink-faint focus:border-accent focus-visible:outline-accent disabled:bg-paper disabled:text-ink-faint";

export function Field({ label, hint, error, className, ...props }: FieldProps) {
  const id = useId();
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="text-sm font-medium">{label}</label>
      <input id={id} aria-invalid={!!error} aria-describedby={hint || error ? `${id}-d` : undefined} className={cx(controlBase, error && "border-stamp", className)} {...props} />
      {(hint || error) && <p id={`${id}-d`} className={cx("text-xs", error ? "text-stamp" : "text-ink-faint")}>{error ?? hint}</p>}
    </div>
  );
}

export function Select({ label, className, children, ...props }: { label?: string } & SelectHTMLAttributes<HTMLSelectElement>) {
  const id = useId();
  return (
    <div className="flex flex-col gap-1.5">
      {label && <label htmlFor={id} className="text-sm font-medium">{label}</label>}
      <div className="relative">
        <select id={id} className={cx(controlBase, "appearance-none pr-9", className)} {...props}>{children}</select>
        <ChevronDown aria-hidden size={16} className="pointer-events-none absolute right-3 top-1/2 -translate-y-1/2 text-ink-faint" />
      </div>
    </div>
  );
}

type Variant = "primary" | "secondary" | "ghost" | "danger";
const variants: Record<Variant, string> = {
  primary: "bg-accent text-white hover:bg-accent-hover border-transparent",
  secondary: "bg-leaf text-ink border-rule-strong hover:bg-paper",
  ghost: "bg-transparent text-ink-soft border-transparent hover:bg-paper-deep hover:text-ink",
  danger: "bg-leaf text-stamp border-rule-strong hover:bg-stamp-soft",
};

export function Button({ variant = "primary", size = "md", loading, className, children, disabled, ...props }:
  { variant?: Variant; size?: "sm" | "md"; loading?: boolean } & ButtonHTMLAttributes<HTMLButtonElement>) {
  return (
    <button
      className={cx(
        "inline-flex shrink-0 items-center justify-center gap-2 whitespace-nowrap rounded-md border font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-50",
        size === "md" ? "h-10 px-4 text-sm" : "h-8 px-3 text-[13px]",
        variants[variant], className,
      )}
      disabled={loading || disabled}
      {...props}
    >
      {loading && <Loader2 size={15} className="animate-spin" aria-hidden />}
      {children}
    </button>
  );
}

const tones = {
  ledger: "bg-ledger-soft text-ledger",
  stamp: "bg-stamp-soft text-stamp",
  pending: "bg-pending-soft text-pending",
  neutral: "bg-paper-deep text-ink-soft",
  accent: "bg-accent-soft text-accent",
};
export type Tone = keyof typeof tones;

export function Badge({ tone = "neutral", children }: { tone?: Tone; children: ReactNode }) {
  return <span className={cx("inline-flex items-center rounded px-2 py-0.5 text-xs font-medium", tones[tone])}>{children}</span>;
}

export function Alert({ tone, children }: { tone: "error" | "success"; children: ReactNode }) {
  const Icon = tone === "error" ? AlertCircle : CheckCircle2;
  return (
    <div role={tone === "error" ? "alert" : "status"}
      className={cx("flex items-start gap-2.5 rounded-md px-3 py-2.5 text-sm", tone === "error" ? "bg-stamp-soft text-stamp" : "bg-ledger-soft text-ledger")}>
      <Icon size={16} className="mt-0.5 shrink-0" aria-hidden />
      <span>{children}</span>
    </div>
  );
}

export const ErrorMessage = ({ message }: { message: string | null }) => (message ? <Alert tone="error">{message}</Alert> : null);
export const SuccessMessage = ({ message }: { message: string | null }) => (message ? <Alert tone="success">{message}</Alert> : null);

export function Skeleton({ className }: { className?: string }) {
  return <div className={cx("skeleton", className)} aria-hidden />;
}

export function EmptyState({ icon, title, children }: { icon: ReactNode; title: string; children?: ReactNode }) {
  return (
    <div className="flex flex-col items-center gap-2 px-6 py-14 text-center">
      <div className="mb-1 flex size-10 items-center justify-center rounded-full bg-paper-deep text-ink-soft">{icon}</div>
      <h2 className="text-[15px] font-semibold">{title}</h2>
      {children && <p className="max-w-sm text-sm text-ink-soft">{children}</p>}
    </div>
  );
}

export function Avatar({ name }: { name: string }) {
  const initials = name.split(/\s+/).filter(Boolean).slice(0, 2).map((p) => p[0]!.toUpperCase()).join("") || "?";
  return <span aria-hidden className="flex size-8 shrink-0 items-center justify-center rounded-full bg-paper-deep text-xs font-semibold text-ink-soft">{initials}</span>;
}

export function Textarea({ label, hint, className, ...props }: { label: string; hint?: string } & TextareaHTMLAttributes<HTMLTextAreaElement>) {
  const id = useId();
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="text-sm font-medium">{label}</label>
      <textarea id={id} rows={3} className={cx(controlBase, "h-auto resize-y py-2", className)} {...props} />
      {hint && <p className="text-xs text-ink-faint">{hint}</p>}
    </div>
  );
}

export function SearchInput({ label, className, ...props }: { label: string } & InputHTMLAttributes<HTMLInputElement>) {
  return (
    <div className="relative">
      <Search aria-hidden size={16} className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-ink-faint" />
      <input type="search" aria-label={label} className={cx(controlBase, "pl-9", className)} {...props} />
    </div>
  );
}

export function Pager({ page, pageSize, total, onPage }: { page: number; pageSize: number; total: number; onPage: (p: number) => void }) {
  if (total <= pageSize) return null;
  const from = (page - 1) * pageSize + 1;
  const to = Math.min(page * pageSize, total);
  return (
    <div className="flex items-center justify-between border-t border-rule px-5 py-3 text-sm text-ink-soft">
      <span>{from}–{to} de {total}</span>
      <div className="flex gap-2">
        <Button variant="secondary" size="sm" disabled={page <= 1} onClick={() => onPage(page - 1)}>Anterior</Button>
        <Button variant="secondary" size="sm" disabled={to >= total} onClick={() => onPage(page + 1)}>Siguiente</Button>
      </div>
    </div>
  );
}

/** Async search-and-pick field. Shows the picked item as a chip; typing searches the server. */
export function EntityPicker<T>({ label, placeholder, selected, onSelect, search, itemKey, renderItem, hint }: {
  label: string; placeholder: string; selected: T | null; onSelect: (item: T | null) => void;
  search: (term: string) => Promise<T[]>; itemKey: (item: T) => string; renderItem: (item: T) => ReactNode; hint?: string;
}) {
  const id = useId();
  const [term, setTerm] = useState("");
  const [open, setOpen] = useState(false);
  const [results, setResults] = useState<{ term: string; items: T[]; error?: boolean } | null>(null);

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    const t = setTimeout(() => {
      search(term).then((items) => { if (!cancelled) setResults({ term, items }); }, () => { if (!cancelled) setResults({ term, items: [], error: true }); });
    }, 200);
    return () => { cancelled = true; clearTimeout(t); };
  }, [term, open, search]);

  if (selected) {
    return (
      <div className="flex flex-col gap-1.5">
        <span className="text-sm font-medium">{label}</span>
        <div className="flex min-h-10 items-center justify-between gap-3 rounded-md border border-rule-strong bg-leaf px-3 py-1.5 text-sm">
          <div className="min-w-0">{renderItem(selected)}</div>
          <button type="button" onClick={() => { onSelect(null); setTerm(""); setResults(null); }} aria-label={`Quitar ${label.toLowerCase()}`}
            className="flex size-7 shrink-0 items-center justify-center rounded text-ink-soft hover:bg-paper-deep hover:text-ink"><X size={15} aria-hidden /></button>
        </div>
      </div>
    );
  }
  const loading = open && (results === null || results.term !== term);
  return (
    <div className="relative flex flex-col gap-1.5">
      <label htmlFor={id} className="text-sm font-medium">{label}</label>
      <input id={id} type="search" role="combobox" aria-expanded={open} aria-controls={`${id}-list`} autoComplete="off" placeholder={placeholder}
        className={controlBase} value={term} onFocus={() => setOpen(true)} onChange={(e) => { setTerm(e.target.value); setOpen(true); }}
        onKeyDown={(e) => { if (e.key === "Escape") setOpen(false); }} onBlur={(e) => { if (!e.currentTarget.parentElement?.contains(e.relatedTarget)) setOpen(false); }} />
      {hint && <p className="text-xs text-ink-faint">{hint}</p>}
      {open && (
        <ul id={`${id}-list`} className="absolute left-0 right-0 top-full z-20 mt-1 max-h-64 overflow-auto rounded-md border border-rule-strong bg-leaf p-1">
          {loading && <li className="px-3 py-2 text-sm text-ink-faint">Buscando…</li>}
          {!loading && results?.error && <li className="px-3 py-2 text-sm text-stamp">No se pudo buscar.</li>}
          {!loading && !results?.error && results?.items.length === 0 && <li className="px-3 py-2 text-sm text-ink-faint">Sin resultados.</li>}
          {!loading && results?.items.map((item) => (
            <li key={itemKey(item)}>
              <button type="button" className="w-full rounded px-3 py-2 text-left text-sm hover:bg-accent-soft/50 focus-visible:bg-accent-soft/50"
                onClick={() => { onSelect(item); setOpen(false); }}>{renderItem(item)}</button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
