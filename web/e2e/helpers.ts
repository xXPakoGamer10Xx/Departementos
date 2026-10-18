import { expect, type APIRequestContext, type Browser, type BrowserContext, type Page } from "@playwright/test";

export const PASSWORD = "Habita-E2E-2026!";
const BASE = process.env.E2E_BASE_URL ?? "http://localhost:3101";
const uid = () => Math.random().toString(36).slice(2, 10);

/** A person signed in inside their own browser context. `api` shares the cookies of `page`, so what the page does and what the helpers do are one session. */
export type Session = { context: BrowserContext; page: Page; api: APIRequestContext };

async function post<T = unknown>(api: APIRequestContext, path: string, data?: unknown): Promise<T> {
  // The API checks the Origin header on requests that change data, as a browser would send it.
  const r = await api.post(`/api/v1${path}`, { data, headers: { Origin: BASE } });
  expect(r.ok(), `${path} → ${r.status()} ${await r.text()}`).toBeTruthy();
  return (r.status() === 204 ? undefined : await r.json().catch(() => undefined)) as T;
}
async function patch<T = unknown>(api: APIRequestContext, path: string, data: unknown): Promise<T> {
  const r = await api.patch(`/api/v1${path}`, { data, headers: { Origin: BASE } });
  expect(r.ok(), `PATCH ${path} → ${r.status()} ${await r.text()}`).toBeTruthy();
  return (await r.json()) as T;
}

export async function newSession(browser: Browser, viewport = { width: 1280, height: 900 }): Promise<Session> {
  const context = await browser.newContext({ viewport, baseURL: BASE });
  return { context, page: await context.newPage(), api: context.request };
}

/** Registers a brand-new organization (its owner is signed in) with one property, so tests never share data. */
export async function newOrganization(browser: Browser) {
  const owner = await newSession(browser);
  const tag = uid();
  await post(owner.api, "/auth/register", { email: `owner-${tag}@example.com`, password: PASSWORD, fullName: "Paola Propietaria", organizationName: `Org E2E ${tag}` });
  const property = await post<{ id: string }>(owner.api, "/properties", { name: "Torre E2E", address: "Calle Falsa 123" });
  return { owner, propertyId: property.id, tag };
}

export const isoDate = (offsetDays: number) => {
  const d = new Date(); d.setDate(d.getDate() + offsetDays);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
};

type Org = Awaited<ReturnType<typeof newOrganization>>;

/** A tenant with an active lease, a pending charge, and a signed-in portal session (access code redeemed as the tenant would). */
export async function addTenant(browser: Browser, org: Org, opts: { name: string; unit: string; rent?: number; chargeDueInDays?: number; chargeAmount?: number }) {
  const rent = opts.rent ?? 8500;
  const unit = await post<{ id: string }>(org.owner.api, `/properties/${org.propertyId}/units`, { code: opts.unit, baseRent: rent });
  const tenant = await post<{ id: string }>(org.owner.api, "/tenants", { fullName: opts.name });
  const lease = await post<{ id: string }>(org.owner.api, "/leases", { unitId: unit.id, tenantId: tenant.id, startDate: isoDate(-90), monthlyRent: rent, dueDay: 5, depositAmount: rent });
  await post(org.owner.api, `/leases/${lease.id}/activate`);
  const charge = await post<{ id: string }>(org.owner.api, "/rent-charges", { leaseId: lease.id, description: "Renta del mes", amount: opts.chargeAmount ?? rent, dueDate: isoDate(opts.chargeDueInDays ?? -1) });
  const { code } = await post<{ code: string }>(org.owner.api, "/access-codes", { role: "Tenant", tenantId: tenant.id });
  const portal = await newSession(browser, { width: 390, height: 844 });
  await post(portal.api, "/auth/access-codes/redeem", { code, email: `tenant-${uid()}@example.com`, fullName: opts.name, password: PASSWORD });
  return { portal, tenantId: tenant.id, leaseId: lease.id, chargeId: charge.id, unitId: unit.id };
}

/** Invites someone with a role and returns them signed in. */
export async function addMember(browser: Browser, org: Org, role: string, viewport?: { width: number; height: number }) {
  const { token } = await post<{ token: string }>(org.owner.api, "/members/invitations", { email: `${role.toLowerCase()}-${uid()}@example.com`, role });
  const member = await newSession(browser, viewport);
  await post(member.api, "/auth/invitations/accept", { token, fullName: role, password: PASSWORD });
  return member;
}

export const api = { post, patch };

/** A one-pixel PNG made unique, so two uploads are never the same file. */
export const proofPng = () => Buffer.concat([
  Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==", "base64"),
  Buffer.from([Math.floor(Math.random() * 255), 7]),
]);
