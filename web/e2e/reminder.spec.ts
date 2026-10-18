import { expect, test } from "@playwright/test";
import { addTenant, api, newOrganization } from "./helpers";

// Needs the worker running (e2e/run.sh starts it with a fast check interval). The tenant's page stays open and the reminder arrives by itself.
test("the worker reminds a tenant of a rent coming due, naming them first", async ({ browser }) => {
  const org = await newOrganization(browser);
  await api.patch(org.owner.api, "/settings", { reminderHour: 0, rentReminderDays: [3] });   // from midnight on, so it is due whatever time the test runs
  const ana = await addTenant(browser, org, { name: "Carla Prueba Núñez", unit: "E2E-5", chargeDueInDays: 3, chargeAmount: 7000 });

  const page = ana.portal.page;
  await page.goto("/portal");
  await expect(page.getByRole("button", { name: /Avisos: 1 sin leer/ })).toBeVisible({ timeout: 60_000 });
  await page.getByRole("button", { name: /^Avisos/ }).first().click();
  await expect(page.getByText("Carla Prueba Núñez, tu renta vence en 3 días")).toBeVisible();

  // Once per day: nothing more arrives for the same organization.
  await page.waitForTimeout(8_000);
  await expect(page.getByRole("button", { name: /Avisos: 1 sin leer/ })).toBeVisible();
});
