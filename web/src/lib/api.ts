export class ApiError extends Error {
  constructor(public status: number, message: string, public fieldErrors?: Record<string, string[]>) {
    super(message);
  }
}

export async function api<T = void>(path: string, init?: { method?: string; body?: unknown; headers?: Record<string, string> }): Promise<T> {
  const res = await fetch(`/api/v1${path}`, {
    method: init?.method ?? (init?.body ? "POST" : "GET"),
    headers: { ...(init?.body ? { "Content-Type": "application/json" } : {}), ...init?.headers },
    body: init?.body ? JSON.stringify(init.body) : undefined,
    credentials: "same-origin",
  });
  if (!res.ok) {
    const problem = await res.json().catch(() => null);
    const fields = problem?.errors as Record<string, string[]> | undefined;
    const message = fields ? Object.values(fields).flat().join(" ") : (problem?.detail ?? problem?.title ?? `Error ${res.status}`);
    throw new ApiError(res.status, message, fields);
  }
  if (res.status === 204 || res.status === 202) return undefined as T;
  return (await res.json().catch(() => undefined)) as T;
}

export type Role = "Owner" | "Admin" | "Finance" | "Maintenance" | "ReadOnly" | "Collector" | "Tenant";
export type MembershipStatus = "Active" | "Deactivated";

export type Me = {
  user: { id: string; email: string; fullName: string };
  activeOrganizationId: string | null;
  activeRole: Role | null;
  activeTenantId?: string | null;
  organizations: { id: string; name: string; slug: string; currency: string; role: Role }[];
};

export type Member = { id: string; userId: string; role: Role; status: MembershipStatus; email: string | null; fullName: string | null; tenantId?: string | null };

export const roleLabels: Record<Role, string> = {
  Owner: "Propietario", Admin: "Administrador", Finance: "Finanzas", Maintenance: "Mantenimiento", ReadOnly: "Solo lectura", Collector: "Cobrador", Tenant: "Inquilino",
};

/** Roles an administrator can hand out by invitation or change to. Tenant only arises from a code tied to a tenant record. */
export const assignableRoles: Role[] = ["Owner", "Admin", "Finance", "Maintenance", "ReadOnly", "Collector"];

export type Paged<T> = { items: T[]; total: number; page: number; pageSize: number };

export type PropertyStatus = "Active" | "Archived";
export type Property = { id: string; name: string; address: string; notes: string | null; status: PropertyStatus; unitCount: number; createdAt: string };

export type UnitType = "Apartment" | "House" | "Room" | "Commercial" | "Other";
export type UnitStatus = "Available" | "Occupied" | "Maintenance" | "Archived";
/** baseRent is absent for roles that may not see financial data (Maintenance). */
export type Unit = { id: string; propertyId: string; code: string; type: UnitType; bedrooms?: number; baseRent?: number; status: UnitStatus };

export const unitTypeLabels: Record<UnitType, string> = {
  Apartment: "Departamento", House: "Casa", Room: "Habitación", Commercial: "Local comercial", Other: "Otro",
};
export const unitStatusLabels: Record<UnitStatus, string> = {
  Available: "Disponible", Occupied: "Ocupada", Maintenance: "En mantenimiento", Archived: "Archivada",
};

export const formatMoney = (amount: number, currency: string) =>
  new Intl.NumberFormat("es-MX", { style: "currency", currency }).format(amount);

export const canManage = (role: Role | null) => role === "Owner" || role === "Admin";

export type TenantStatus = "Active" | "Archived";
export type Tenant = { id: string; fullName: string; email: string | null; phone: string | null; notes: string | null; status: TenantStatus; createdAt: string };
/** Tenants hold personal data; the Maintenance role has no access. */
export const canReadTenants = (role: Role | null) => role !== null && role !== "Maintenance" && role !== "Tenant";
/** Contracts carry rent and identity: the Collector does not get them. */
export const canReadLeases = (role: Role | null) => role !== null && canReadTenants(role) && role !== "Collector";
/** Reports and exports: not for the Collector. */
export const canReadReports = canReadLeases;
/** Everyone except the Collector (field role) and the Tenant (portal login) works with properties, dashboard and maintenance. */
export const isStaff = (role: Role | null) => role !== null && role !== "Collector" && role !== "Tenant";

export type LeaseStatus = "Draft" | "Active" | "Ended" | "Cancelled";
export type Lease = {
  id: string; unitId: string; unitCode: string; propertyId: string; propertyName: string; tenantId: string; tenantName: string;
  startDate: string; endDate: string | null; monthlyRent: number; dueDay: number; depositAmount: number | null;
  status: LeaseStatus; termsNotes: string | null; closureReason: string | null; isCurrent: boolean; createdAt: string;
  guarantorName: string | null; guarantorPhone: string | null;
};
export type UnitSearch = { id: string; code: string; propertyId: string; propertyName: string; type: UnitType; baseRent?: number };

export const leaseStatusLabels: Record<LeaseStatus, string> = { Draft: "Borrador", Active: "Vigente", Ended: "Terminado", Cancelled: "Cancelado" };

/** Dates travel as yyyy-MM-dd local dates; parse by parts so the browser time zone never shifts them. */
export function formatDate(iso: string): string {
  const [y, m, d] = iso.split("-").map(Number);
  return new Date(y, m - 1, d).toLocaleDateString("es-MX", { day: "numeric", month: "short", year: "numeric" });
}
export const todayIso = () => {
  const n = new Date();
  return `${n.getFullYear()}-${String(n.getMonth() + 1).padStart(2, "0")}-${String(n.getDate()).padStart(2, "0")}`;
};
export const formatPeriod = (l: Pick<Lease, "startDate" | "endDate">) =>
  `${formatDate(l.startDate)} – ${l.endDate ? formatDate(l.endDate) : "sin fecha final"}`;

export type ChargeStatus = "Open" | "PartiallyPaid" | "Paid" | "Overdue" | "Voided";
export type ChargeType = "Rent" | "Other" | "Deposit";
export type Charge = {
  id: string; leaseId: string; unitId: string; unitCode: string; propertyId: string; propertyName: string; tenantId: string; tenantName: string;
  type: ChargeType; periodYear: number | null; periodMonth: number | null; description: string; amount: number; paidAmount: number; balance: number;
  dueDate: string; status: ChargeStatus; voidReason: string | null; createdAt: string;
};
export type PreviewAction = "Create" | "AlreadyExists" | "SkippedZeroRent";
export type PreviewItem = { leaseId: string; unitCode: string; propertyName: string; tenantName: string; amount: number; dueDate: string; action: PreviewAction };
export type GenerationPreview = { year: number; month: number; items: PreviewItem[]; toCreate: number; toCreateTotal: number; alreadyExist: number; skipped: number };
export type GenerationResult = { year: number; month: number; created: number; alreadyExisted: number; skipped: number; createdTotal: number };

export const chargeStatusLabels: Record<ChargeStatus, string> = {
  Open: "Pendiente", PartiallyPaid: "Pago parcial", Paid: "Pagado", Overdue: "Vencido", Voided: "Anulado",
};
/** Financial data: Maintenance has no access. Finance operates; voiding is Owner/Admin only. */
export const canReadFinance = canReadTenants;
export const canWriteFinance = (role: Role | null) => role === "Owner" || role === "Admin" || role === "Finance";
/** Recording a payment is also what a Collector does; creating charges is not. */
export const canWritePayments = (role: Role | null) => canWriteFinance(role) || role === "Collector";

const monthNames = ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];
export const formatMonth = (year: number, month: number) => `${monthNames[month - 1]} ${year}`;
export const currentMonthValue = () => { const n = new Date(); return `${n.getFullYear()}-${String(n.getMonth() + 1).padStart(2, "0")}`; };
export const parseMonthValue = (v: string): { year: number; month: number } | null => {
  const m = /^(\d{4})-(\d{2})$/.exec(v); return m ? { year: Number(m[1]), month: Number(m[2]) } : null;
};

export type PaymentMethod = "Cash" | "BankTransfer" | "Card" | "Other";
export type PaymentStatus = "Confirmed" | "Reversed";
export type Allocation = {
  id: string; chargeId: string; chargeDescription: string; unitCode: string; propertyName: string; amount: number;
  createdAt: string; reversedAt: string | null; reversalReason: string | null;
};
export type Payment = {
  id: string; tenantId: string; tenantName: string; receivedOn: string; amount: number; method: PaymentMethod; reference: string | null; notes: string | null;
  status: PaymentStatus; allocatedAmount: number; unassignedAmount: number; createdAt: string; reversedAt: string | null; reversalReason: string | null;
  allocations: Allocation[] | null; receiptFolio?: string | null;
};
export const paymentMethodLabels: Record<PaymentMethod, string> = { Cash: "Efectivo", BankTransfer: "Transferencia", Card: "Tarjeta", Other: "Otro" };
export const paymentStatusLabels: Record<PaymentStatus, string> = { Confirmed: "Confirmado", Reversed: "Revertido" };

/** Money math in integer cents: never compare or sum currency with floating point. */
export const toCents = (v: string | number): number => Math.round(Number(v || 0) * 100);
export const fromCents = (c: number): number => c / 100;

export type IssuePriority = "Low" | "Normal" | "High" | "Urgent";
export type IssueStatus = "Open" | "InProgress" | "Waiting" | "Resolved" | "Closed";
export type IssueEventType = "Created" | "StatusChanged" | "Comment" | "Assigned" | "Updated";
export type IssueEvent = {
  id: string; type: IssueEventType; actorName: string | null; fromStatus: IssueStatus | null; toStatus: IssueStatus | null;
  targetName: string | null; body: string | null; createdAt: string; tenantVisible?: boolean;
};
/** Operational data only: the API never sends rents, payments or tenant identity in maintenance issues. */
export type Issue = {
  id: string; propertyId: string; propertyName: string; unitId: string | null; unitCode: string | null; title: string; description: string;
  priority: IssuePriority; status: IssueStatus; reportedAt: string; assignedToUserId: string | null; assignedToName: string | null;
  resolvedAt: string | null; resolvedByName: string | null; createdByName: string | null; updatedAt: string; events: IssueEvent[] | null; reportedByTenant?: boolean;
};
export const issuePriorityLabels: Record<IssuePriority, string> = { Low: "Baja", Normal: "Normal", High: "Alta", Urgent: "Urgente" };
export const issueStatusLabels: Record<IssueStatus, string> = { Open: "Abierta", InProgress: "En proceso", Waiting: "En espera", Resolved: "Resuelta", Closed: "Cerrada" };

const transitions: Record<IssueStatus, IssueStatus[]> = {
  Open: ["InProgress", "Waiting", "Resolved"],
  InProgress: ["Open", "Waiting", "Resolved"],
  Waiting: ["Open", "InProgress", "Resolved"],
  Resolved: ["Closed", "InProgress"],
  Closed: [],
};
/** Mirrors the server's rules (the API is the authority): Owner/Admin any valid move; Maintenance only on their own issues and never Closed/Open. */
export function nextStatuses(issue: Pick<Issue, "status" | "assignedToUserId">, role: Role | null, myUserId: string): IssueStatus[] {
  const valid = transitions[issue.status];
  if (role === "Owner" || role === "Admin") return valid;
  if (role === "Maintenance" && issue.assignedToUserId === myUserId) return valid.filter((s) => s === "InProgress" || s === "Waiting" || s === "Resolved");
  return [];
}
export const canActOnIssue = (issue: Pick<Issue, "status" | "assignedToUserId">, role: Role | null, myUserId: string) =>
  issue.status !== "Closed" && (role === "Owner" || role === "Admin" || (role === "Maintenance" && issue.assignedToUserId === myUserId));

/** Where each role lands after signing in. */
export const homePath = (role: Role | null) =>
  role === "Maintenance" ? "/maintenance" : role === "Collector" ? "/collect" : role === "Tenant" ? "/portal" : "/dashboard";

export const formatDateTime = (iso: string) =>
  new Date(iso).toLocaleString("es-MX", { day: "numeric", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit" });

export type AgingBucket = { label: string; fromDays: number; toDays: number | null; count: number; balance: number };
export type MoneyCount = { count: number; total: number };
export type MonthRow = { year: number; month: number; rentCharged: number; otherCharged: number; received: number; reversed: number; chargesCount: number; paymentsCount: number };
export type FinancialSummary = {
  from: string; to: string; currency: string; asOf: string;
  paymentsReceived: MoneyCount; paymentsReversed: MoneyCount; rentChargesGenerated: MoneyCount; otherChargesGenerated: MoneyCount; chargesVoided: MoneyCount;
  depositsHeld: number; depositsReceived: number; depositsReturned: number; depositsRetained: number;
  pendingBalance: number; openCharges: number; overdueCharges: number; overdueBalance: number; tenantsWithOverdue: number; unassignedCredit: number;
  byMonth: MonthRow[] | null; overdueAging: AgingBucket[];
};
export type Occupancy = { totalUnits: number; occupied: number; maintenance: number; available: number; occupancyRate: number };
export type IssuesByPriority = { urgent: number; high: number; normal: number; low: number; total: number };
export type Activity = { id: string; action: string; label: string; entityType: string; entityId: string; actorName: string | null; occurredAt: string };
/** finance is null for roles that may not see financial data (Maintenance). */
export type Dashboard = {
  year: number; month: number; from: string; to: string; currency: string; timeZone: string; asOf: string;
  finance: FinancialSummary | null; occupancy: Occupancy; issues: IssuesByPriority; activity: Activity[];
};

/** First and last day (yyyy-MM-dd) of a yyyy-MM month value. */
export function monthBounds(value: string): { from: string; to: string } | null {
  const p = parseMonthValue(value);
  if (!p) return null;
  const last = new Date(p.year, p.month, 0).getDate();
  const mm = String(p.month).padStart(2, "0");
  return { from: `${p.year}-${mm}-01`, to: `${p.year}-${mm}-${String(last).padStart(2, "0")}` };
}
export const monthValueAgo = (months: number) => { const n = new Date(); n.setDate(1); n.setMonth(n.getMonth() - months); return `${n.getFullYear()}-${String(n.getMonth() + 1).padStart(2, "0")}`; };

export function activityHref(a: Pick<Activity, "entityType" | "entityId">): string | null {
  switch (a.entityType) {
    case "Payment": return `/payments/${a.entityId}`;
    case "Lease": return `/leases/${a.entityId}`;
    case "Tenant": return `/tenants/${a.entityId}`;
    case "Property": return `/properties/${a.entityId}`;
    case "MaintenanceIssue": return `/maintenance/${a.entityId}`;
    case "RentCharge": case "ChargeBatch": return "/charges";
    case "PaymentClaim": return "/claims";
    case "PaymentPromise": return "/promises";
    case "BankAccount": return "/settings/bank-accounts";
    default: return null;
  }
}

export type AuditEntry = {
  id: string; action: string; label: string; entityType: string; entityId: string; actorUserId: string; actorName: string | null;
  occurredAt: string; reason: string | null; metadata: Record<string, unknown> | null;
};
export const auditEntityTypes: Record<string, string> = {
  Property: "Propiedad", Unit: "Unidad", Tenant: "Inquilino", Lease: "Contrato", RentCharge: "Cargo", ChargeBatch: "Lote de cargos", Payment: "Pago",
  MaintenanceIssue: "Incidencia", Membership: "Miembro", Invitation: "Invitación", Organization: "Organización", Report: "Reporte",
  BankAccount: "Cuenta bancaria", PaymentClaim: "Reporte de pago", PaymentPromise: "Promesa de pago", CashToken: "Código de efectivo",
};

export const formatCompactMoney = (amount: number, currency: string) =>
  new Intl.NumberFormat("es-MX", { style: "currency", currency, notation: "compact", maximumFractionDigits: 1 }).format(amount);
export const monthShort = (year: number, month: number) => new Date(year, month - 1, 1).toLocaleDateString("es-MX", { month: "short" }).replace(".", "");

// ---------- Tenant portal ----------
export type PortalLease = { id: string; unitCode: string; propertyName: string; propertyAddress: string; monthlyRent: number; dueDay: number; startDate: string; endDate: string | null };
export type PortalCharge = {
  id: string; description: string; type: ChargeType; periodYear: number | null; periodMonth: number | null;
  amount: number; paid: number; balance: number; dueDate: string; status: ChargeStatus;
};
export type PortalSummary = {
  tenantName: string; currency: string; today: string; lease: PortalLease | null; totalDue: number; overdueAmount: number; overdueCount: number;
  nextCharge: PortalCharge | null; creditBalance: number; hasContract?: boolean;
};
export type PortalPayment = {
  id: string; receivedOn: string; amount: number; method: PaymentMethod; reference: string | null; status: PaymentStatus; unassigned: number;
  allocations: { description: string; amount: number }[]; receiptFolio?: string | null;
};
export type PortalIssueEvent = { type: IssueEventType; fromTenant: boolean; toStatus: IssueStatus | null; body: string | null; createdAt: string };
export type PortalIssue = {
  id: string; title: string; description: string; status: IssueStatus; unitCode: string | null; reportedAt: string; updatedAt: string;
  resolvedAt: string | null; events: PortalIssueEvent[] | null;
};
export const isTenant = (role: Role | null) => role === "Tenant";

// ---------- Collections: bank accounts, payment reports, promises, cash QR ----------
export type BankAccount = {
  id: string; propertyId: string | null; propertyName: string | null; holderName: string; bankName: string; clabe: string; clabeFormatted: string;
  isDefault: boolean; status: "Active" | "Archived";
};
export type PortalPaymentInfo = { landlordName: string; account: { holderName: string; bankName: string; clabe: string; clabeFormatted: string } | null };

export type ClaimStatus = "Pending" | "Confirmed" | "Rejected" | "Cancelled";
export const claimStatusLabels: Record<ClaimStatus, string> = { Pending: "En revisión", Confirmed: "Confirmado", Rejected: "Rechazado", Cancelled: "Cancelado" };
/** What a tenant may report. Card is not something a tenant reports; staff register those. */
export const reportableMethods: PaymentMethod[] = ["BankTransfer", "Cash", "Other"];
export type PaymentClaim = {
  id: string; tenantId: string; tenantName: string; amount: number; paidOn: string; method: PaymentMethod; reference: string | null; tenantNote: string | null;
  file: { id: string; name: string; contentType: string; sizeBytes: number }; status: ClaimStatus; createdAt: string; reviewedAt: string | null;
  reviewedByName: string | null; reviewComment: string | null; paymentId: string | null; duplicateProof: boolean;
};
export type TenantClaim = {
  id: string; amount: number; paidOn: string; method: PaymentMethod; reference: string | null; tenantNote: string | null; fileName: string;
  status: ClaimStatus; createdAt: string; reviewedAt: string | null; reviewComment: string | null;
};

export type PromiseStatus = "Pending" | "Kept" | "Broken" | "Cancelled";
export const promiseStatusLabels: Record<PromiseStatus, string> = { Pending: "Pendiente", Kept: "Cumplida", Broken: "Incumplida", Cancelled: "Cancelada" };
export type PaymentPromise = {
  id: string; tenantId: string; tenantName: string; chargeId: string; chargeDescription: string; chargeAmount: number; chargeBalance: number; dueDate: string;
  promisedOn: string; note: string | null; status: PromiseStatus; createdAt: string; cancelReason: string | null;
};

export type CashCode = { id: string; code: string; qrPayload: string; expiresAt: string };
export type CashTokenInfo = {
  tokenId: string; tenantId: string; tenantName: string; expiresAt: string; currency: string;
  charges: { chargeId: string; description: string; dueDate: string; amount: number; balance: number; status: ChargeStatus; unitCode: string; propertyName: string }[];
};

// ---------- Notifications ----------
export type NotificationType =
  | "ClaimSubmitted" | "ClaimConfirmed" | "ClaimRejected" | "IssueReported" | "CashCollected"
  | "RentDueSoon" | "ChargeOverdue" | "LeaseExpiring" | "PromiseDue" | "PromiseBroken";
export type AppNotification = { id: string; type: NotificationType; title: string; body: string; url: string; createdAt: string; read: boolean };
export type NotificationList = { items: AppNotification[]; total: number; page: number; pageSize: number; unread: number };
export type NotificationPrefs = { emailEnabled: boolean; pushEnabled: boolean };
export type PushConfig = { enabled: boolean; publicKey: string | null };

/** "hace 5 min", "hace 2 h", "ayer"… from an ISO timestamp. Falls back to the date for anything older than a week. */
export function timeAgo(iso: string, now = Date.now()): string {
  const seconds = Math.round((new Date(iso).getTime() - now) / 1000);
  const abs = Math.abs(seconds);
  const rtf = new Intl.RelativeTimeFormat("es-MX", { numeric: "auto" });
  if (abs < 45) return "ahora";
  if (abs < 3600) return rtf.format(Math.round(seconds / 60), "minute");
  if (abs < 86400) return rtf.format(Math.round(seconds / 3600), "hour");
  if (abs < 7 * 86400) return rtf.format(Math.round(seconds / 86400), "day");
  return new Date(iso).toLocaleDateString("es-MX", { day: "numeric", month: "short", year: "numeric" });
}

// ---------- Documents: contract template, generated contracts, receipts ----------
export type ContractTemplate = { id: string; name: string; fields: string[]; missingRecommended: string[]; isActive: boolean; createdAt: string; retiredAt: string | null };
export type ContractField = { name: string; description: string; example: string };
export type LeaseDocument = {
  id: string; leaseId: string; version: number; templateName: string; generatedAt: string; generatedByName: string | null;
  docxBytes: number; pdfBytes: number; docxSha256: string; pdfSha256: string;
};
export const canGenerateContracts = canManage;

export type ItemCondition = "Good" | "Fair" | "Poor";
export const conditionLabels: Record<ItemCondition, string> = { Good: "Bueno", Fair: "Regular", Poor: "Malo" };
export type InventoryItem = { id: string; name: string; quantity: number; condition: ItemCondition; notes: string | null; returnCondition: ItemCondition | null; returnNotes: string | null };

export type DepositInstallment = { chargeId: string; description: string; dueDate: string; amount: number; paid: number; balance: number; status: ChargeStatus };
export type DepositSettlement = { id: string; held: number; deductionsTotal: number; refund: number; settledOn: string; notes: string | null; deductions: { concept: string; amount: number }[]; createdAt: string; voidedAt: string | null; voidReason: string | null };
export type Deposit = {
  leaseId: string; leaseStatus: LeaseStatus; depositAmount: number | null; scheduled: number; unscheduled: number; paid: number; outstanding: number; custody: number;
  installments: DepositInstallment[]; settlement: DepositSettlement | null; voidedSettlements: DepositSettlement[];
};

export type ImportRow = {
  row: number; name: string; unitCode: string; monthlyRent: number | null; dueDay: number | null; startDate: string | null; endDate: string | null; guarantorName: string | null; phone: string | null;
  action: "Create" | "AlreadyImported" | "Error"; newTenant: boolean; newUnit: boolean; errors: string[]; warnings: string[];
};
export type ImportPreview = { sheetName: string; planHash: string; rows: ImportRow[]; unknownColumns: string[]; toCreate: number; alreadyImported: number; errors: number; newTenants: number; newUnits: number; canConfirm: boolean };
export type ImportResult = { leasesCreated: number; tenantsCreated: number; unitsCreated: number; leasesActivated: number; alreadyImported: number; skippedWithErrors: number };

/** Sends a file (multipart) to the API. The browser sets the multipart boundary itself, so no Content-Type is given. */
export async function apiUpload<T>(path: string, form: FormData): Promise<T> {
  const res = await fetch(`/api/v1${path}`, { method: "POST", body: form, credentials: "same-origin" });
  if (!res.ok) {
    const problem = await res.json().catch(() => null);
    throw new ApiError(res.status, problem?.detail ?? problem?.title ?? `Error ${res.status}`);
  }
  return (await res.json()) as T;
}
