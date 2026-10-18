"use client";
import { Plus, Wrench } from "lucide-react";
import Link from "next/link";
import { useState } from "react";
import { Badge, EmptyState, ErrorMessage, Leaf, Pager, Skeleton } from "@/components/ui";
import { api, formatDateTime, issueStatusLabels, type Paged, type PortalIssue } from "@/lib/api";
import { issueStatusTone } from "@/lib/issue-ui";
import { useQuery } from "@/lib/use-query";

export default function PortalIssuesPage() {
  const [page, setPage] = useState(1);
  const { data, error } = useQuery(`portal:issues:${page}`, () => api<Paged<PortalIssue>>(`/portal/issues?page=${page}&pageSize=10`));
  return (
    <div className="flex flex-col gap-6">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Reportar una falla</h1>
          <p className="mt-1 text-sm text-ink-soft">Tus reportes y cómo van.</p>
        </div>
        <Link href="/portal/issues/new" className="inline-flex h-10 shrink-0 items-center gap-1.5 rounded-md bg-accent px-4 text-sm font-medium text-white hover:opacity-90"><Plus size={15} aria-hidden /> Nuevo</Link>
      </div>
      <ErrorMessage message={error?.message ?? null} />
      <Leaf className="overflow-hidden">
        {!data && !error && <div className="flex flex-col gap-3 p-5">{[0, 1].map((i) => <Skeleton key={i} className="h-12" />)}</div>}
        {data?.items.length === 0 && <EmptyState icon={<Wrench size={18} />} title="No has reportado nada">Si algo se descompone en tu departamento, repórtalo y le damos seguimiento.</EmptyState>}
        {data && data.items.length > 0 && (
          <ul>
            {data.items.map((i) => (
              <li key={i.id} className="border-b border-rule last:border-b-0">
                <Link href={`/portal/issues/${i.id}`} className="flex items-start justify-between gap-4 px-5 py-4 hover:bg-paper">
                  <div className="min-w-0">
                    <p className="truncate text-sm font-medium">{i.title}</p>
                    <p className="mt-0.5 text-xs text-ink-faint">{formatDateTime(i.reportedAt)}</p>
                  </div>
                  <Badge tone={issueStatusTone[i.status]}>{issueStatusLabels[i.status]}</Badge>
                </Link>
              </li>
            ))}
          </ul>
        )}
        {data && <Pager page={page} pageSize={10} total={data.total} onPage={setPage} />}
      </Leaf>
    </div>
  );
}
