import { expect, test } from "@playwright/test";
import { addTenant, api, isoDate, newOrganization, proofPng } from "./helpers";

// The tenant reports a transfer with its proof; the office is told at once (no reload), confirms it, and the tenant is told in turn.
test("tenant uploads a proof, staff confirms it, both are notified live", async ({ browser }) => {
  const org = await newOrganization(browser);
  await api.post(org.owner.api, "/bank-accounts", { holderName: "Paola Propietaria", clabe: "012180001123456781" });
  const ana = await addTenant(browser, org, { name: "Ana Prueba García", unit: "E2E-1", chargeAmount: 1200, chargeDueInDays: 10 });

  const owner = org.owner.page;
  await owner.goto("/dashboard");
  const bell = owner.getByRole("button", { name: /^Avisos/ }).first();
  await expect(bell).toBeVisible();

  const tenant = ana.portal.page;
  await tenant.goto("/portal/pay");
  await tenant.getByRole("button", { name: /ya pagué/i }).click();
  await tenant.getByLabel(/importe/i).fill("1200");
  await tenant.getByLabel("Comprobante").setInputFiles({ name: "comprobante.png", mimeType: "image/png", buffer: proofPng() });
  await tenant.getByRole("button", { name: /enviar reporte/i }).click();
  await expect(tenant.getByText("Reporte enviado")).toBeVisible();

  // The owner's page was never reloaded: the notice arrives over the live stream, and it starts with the person's name.
  await expect(owner.getByRole("button", { name: /Avisos: \d+ sin leer/ })).toBeVisible({ timeout: 30_000 });
  await bell.click();
  await expect(owner.getByRole("dialog", { name: "Avisos" }).getByText("Ana Prueba García reportó un pago")).toBeVisible();
  await owner.getByRole("dialog", { name: "Avisos" }).getByRole("button", { name: /Ana Prueba García reportó un pago/ }).first().click();
  await owner.waitForURL(/\/claims/);

  const card = owner.locator("section").filter({ hasText: "$1,200.00" }).first();
  await card.getByRole("button", { name: "Confirmar pago" }).click();
  await card.getByRole("button", { name: /aplicar a los más antiguos/i }).click();
  await card.getByRole("button", { name: "Confirmar y registrar" }).click();
  await expect(owner.getByText("Pago confirmado y registrado.")).toBeVisible();

  await expect(tenant.getByRole("button", { name: /Avisos: \d+ sin leer/ })).toBeVisible({ timeout: 30_000 });
  await tenant.getByRole("button", { name: /^Avisos/ }).first().click();
  await expect(tenant.getByText("Ana Prueba García, tu pago de $1,200.00 fue confirmado")).toBeVisible();

  // The charge is paid and the tenant owes nothing.
  const charge = await (await org.owner.api.get(`/api/v1/rent-charges/${ana.chargeId}`)).json();
  expect(charge.status).toBe("Paid");
});

test("a second confirmation of the same claim is refused", async ({ browser }) => {
  const org = await newOrganization(browser);
  const ana = await addTenant(browser, org, { name: "Luis Prueba Soto", unit: "E2E-2", chargeAmount: 500 });
  const upload = await ana.portal.api.post("/api/v1/files", { headers: { Origin: process.env.E2E_BASE_URL ?? "http://localhost:3101" }, multipart: { file: { name: "p.png", mimeType: "image/png", buffer: proofPng() } } });
  expect(upload.ok()).toBeTruthy();
  const file = (await upload.json()).id;
  const claim = await api.post<{ id: string }>(ana.portal.api, "/portal/payment-claims", { amount: 500, method: "BankTransfer", paidOn: isoDate(-1), reference: "SPEI-E2E", fileId: file });
  const body = { allocations: [{ chargeId: ana.chargeId, amount: 500 }] };
  const first = await org.owner.api.post(`/api/v1/payment-claims/${claim.id}/confirm`, { data: body, headers: { Origin: process.env.E2E_BASE_URL ?? "http://localhost:3101" } });
  expect(first.status()).toBeLessThan(300);
  const second = await org.owner.api.post(`/api/v1/payment-claims/${claim.id}/confirm`, { data: body, headers: { Origin: process.env.E2E_BASE_URL ?? "http://localhost:3101" } });
  expect(second.status()).toBe(409);
});
