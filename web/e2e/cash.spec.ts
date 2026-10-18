import { expect, test } from "@playwright/test";
import { addMember, addTenant, api, newOrganization } from "./helpers";

// The tenant shows a one-time code; the collector types it on a phone, takes the cash, and the code is spent.
test("a collector collects cash with the tenant's code, once", async ({ browser }) => {
  const org = await newOrganization(browser);
  const ana = await addTenant(browser, org, { name: "Rosa Prueba Díaz", unit: "E2E-3", rent: 8500, chargeAmount: 8500 });
  const collector = await addMember(browser, org, "Collector", { width: 390, height: 844 });
  const { code } = await api.post<{ code: string }>(ana.portal.api, "/portal/cash-token");

  const c = collector.page;
  await c.goto("/collect");
  await c.getByRole("button", { name: /escribir código/i }).click();
  await c.getByLabel("Código del inquilino").fill(code.toLowerCase());     // case does not matter
  await c.getByRole("button", { name: "Buscar" }).click();
  await expect(c.getByText("Cargos pendientes")).toBeVisible();
  await expect(c.getByText("Rosa Prueba Díaz").first()).toBeVisible();
  await c.getByLabel(/dinero recibido/i).fill("8500");
  await c.getByRole("button", { name: /aplicar a los más antiguos/i }).click();
  await c.getByRole("button", { name: /^Cobrar/ }).click();
  await expect(c.getByText("Cobro registrado")).toBeVisible();

  // The code is now spent.
  await c.getByRole("button", { name: /cobrar a otra persona/i }).click();
  await c.getByRole("button", { name: /escribir código/i }).click();
  await c.getByLabel("Código del inquilino").fill(code);
  await c.getByRole("button", { name: "Buscar" }).click();
  await expect(c.getByText("Código inválido o vencido.")).toBeVisible();

  const charge = await (await org.owner.api.get(`/api/v1/rent-charges/${ana.chargeId}`)).json();
  expect(charge.status).toBe("Paid");
});

test("a collector cannot reach the office screens, and a tenant cannot reach the collector's", async ({ browser }) => {
  const org = await newOrganization(browser);
  const ana = await addTenant(browser, org, { name: "Marta Prueba Ruiz", unit: "E2E-4" });
  const collector = await addMember(browser, org, "Collector", { width: 390, height: 844 });
  expect((await collector.api.get("/api/v1/payments")).status()).toBe(200);   // the collector reads payments to collect
  expect((await collector.api.get("/api/v1/leases")).status()).toBe(403);
  expect((await collector.api.get("/api/v1/audit-logs")).status()).toBe(403);
  expect((await collector.api.get("/api/v1/reports/financial-summary?from=2026-01-01&to=2026-12-31")).status()).toBe(403);
  expect((await ana.portal.api.post("/api/v1/cash-tokens/resolve", { data: { code: "ABCD-EFGH" }, headers: { Origin: process.env.E2E_BASE_URL ?? "http://localhost:3101" } })).status()).toBe(403);
  expect((await ana.portal.api.get("/api/v1/leases")).status()).toBe(403);
});
