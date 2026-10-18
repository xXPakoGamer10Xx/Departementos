"use client";
import { ChevronRight, HandCoins, Plus } from "lucide-react";
import Link from "next/link";
import { useEffect, useState } from "react";
import { useMe } from "@/components/me-context";
import { Badge, EmptyState, ErrorMessage, Leaf, Pager, SearchInput, Select, Skeleton } from "@/components/ui";
import { api, canReadFinance, canWritePayments, formatDate, formatMoney, paymentMethodLabels, paymentStatusLabels, type Paged, type Payment, type PaymentStatus } from "@/lib/api";
import { useQuery } from "@/lib/use-query";

const PAGE_SIZE = 25;
const cols = "md:grid-cols-[minmax(0,1.2fr)_110px_minmax(0,1fr)_110px_110px_100px_16px]";

export default function PaymentsPage() {
  const me = useMe();
  const currency = me.organizations.find((o) => o.id === me.activeOrganizationId)?.currency ?? "MXN";
  const [search, setSearch] = useState("");
  const [debounced, setDebounced] = useState("");
  const [status, setStatus] = useState<"" | PaymentStatus>("");
  const [page, setPage] = useState(1);

  useEffect(() => {
    const t = setTimeout(() => { setDebounced(search); setPage(1); }, 250);
    return () => clearTimeout(t);
  }, [search]);

  const params = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
  if (debounced) params.set("search", debounced);
  if (status) params.set("status", status);
  const qs = params.toString();
  const { data, error, loading } = useQuery(qs, () => api<Paged<Payment>>(`/payments?${qs}`));

  if (!canReadFinance(me.activeRole)) {
    return <Leaf><EmptyState icon={<HandCoins size={18} />} title="No tienes permiso para ver pagos">La información financiera no está disponible para tu rol.</EmptyState></Leaf>;
  }

  return (
    <div className="flex flex-col gap-6">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Pagos</h1>
          <p className="mt-1 text-sm text-ink-soft">Pagos recibidos, lo aplicado a cargos y el saldo a favor de cada inquilino.</p>
        </div>
        {canWritePayments(me.activeRole) && (
          <Link href="/payments/new" className="inline-flex h-10 shrink-0 items-center gap-2 whitespace-nowrap rounded-md bg-accent px-4 text-sm font-medium text-white transition-colors hover:bg-accent-hover"><Plus size={16} aria-hidden /> Registrar pago</Link>
        )}
      </div>

      <Leaf className="overflow-hidden">
        <div className="grid gap-3 border-b border-rule p-4 sm:grid-cols-[minmax(0,1fr)_200px]">
          <SearchInput label="Buscar pagos" placeholder="Buscar por inquilino o referencia" value={search} onChange={(e) => setSearch(e.target.value)} />
          <Select aria-label="Estado del pago" value={status} onChange={(e) => { setStatus(e.target.value as "" | PaymentStatus); setPage(1); }}>
            <option value="">Todos los estados</option>
            {(Object.keys(paymentStatusLabels) as PaymentStatus[]).map((s) => <option key={s} value={s}>{paymentStatusLabels[s]}</option>)}
          </Select>
        </div>
        <div aria-hidden className={`hidden gap-4 border-b border-rule bg-paper px-5 py-2.5 text-xs font-medium text-ink-soft md:grid ${cols}`}>
          <span>Inquilino</span><span>Fecha</span><span>Método</span><span className="text-right">Importe</span><span className="text-right">Saldo a favor</span><span>Estado</span><span />
        </div>
        {error && <div className="p-5"><ErrorMessage message={error.message} /></div>}
        {!data && !error && <div className="flex flex-col gap-3 p-5">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-12" />)}</div>}
        {data && data.items.length === 0 && (
          <EmptyState icon={<HandCoins size={18} />} title={debounced || status ? "Sin resultados" : "Aún no hay pagos"}>
            {debounced || status ? "Prueba con otros filtros." : canWritePayments(me.activeRole) ? "Registra el primer pago de un inquilino." : undefined}
          </EmptyState>
        )}
        {data && data.items.length > 0 && (
          <ul className={loading ? "opacity-60 transition-opacity" : "transition-opacity"}>
            {data.items.map((p) => (
              <li key={p.id} className="border-b border-rule last:border-b-0">
                <Link href={`/payments/${p.id}`} className={`group grid items-center gap-x-4 gap-y-1 px-5 py-3.5 transition-colors hover:bg-accent-soft/40 ${cols}`}>
                  <p className="truncate text-sm font-medium">{p.tenantName}</p>
                  <p className="text-sm text-ink-soft">{formatDate(p.receivedOn)}</p>
                  <p className="truncate text-sm text-ink-soft">{paymentMethodLabels[p.method]}{p.reference ? ` · ${p.reference}` : ""}</p>
                  <p className={`text-sm font-medium md:text-right ${p.status === "Reversed" ? "text-ink-faint line-through" : ""}`}>{formatMoney(p.amount, currency)}</p>
                  <p className={`text-sm md:text-right ${p.unassignedAmount > 0 ? "font-medium text-pending" : "text-ink-faint"}`}>
                    <span className="text-ink-soft md:hidden">Saldo a favor: </span>{formatMoney(p.unassignedAmount, currency)}
                  </p>
                  <div><Badge tone={p.status === "Reversed" ? "stamp" : "ledger"}>{paymentStatusLabels[p.status]}</Badge></div>
                  <ChevronRight size={16} className="hidden text-ink-faint transition-transform group-hover:translate-x-0.5 md:block" aria-hidden />
                </Link>
              </li>
            ))}
          </ul>
        )}
        {data && <Pager page={data.page} pageSize={data.pageSize} total={data.total} onPage={setPage} />}
      </Leaf>
    </div>
  );
}
