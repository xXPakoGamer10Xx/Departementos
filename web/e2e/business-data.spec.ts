import { expect, test } from "@playwright/test";
import { addTenant, api, isoDate, newOrganization } from "./helpers";

const csv = (start: string) => "Nombre,Depto,Renta,Con letra,Fecha de pago,Inicio,Término,Fiador,Teléfonos,Observaciones\n" +
  `Rosa Importada Prueba,IMP-1,"$6,500.00",seis mil,05 de cada mes,${start},,Mario Aval,55 4444 5555 / 55 6666 7777,Sin mascotas\n` +
  `Tomás Importado Demo,IMP-2,7200,x,10,${start},,,,\n` +
  `,IMP-3,5000,x,5,${start},,,,\n`;

test("import a spreadsheet (preview, skip the bad row), then schedule the deposit in installments", async ({ browser }) => {
  const org = await newOrganization(browser);
  const p = org.owner.page;
  const [d, m, y] = isoDate(0).split("-").reverse();
  const file = { name: "inquilinos.csv", mimeType: "text/csv", buffer: Buffer.from(csv(`01/${m}/${y}`)) };

  await p.goto("/settings/import");
  await p.getByLabel("Archivo").setInputFiles(file);
  await p.getByLabel("Propiedad").selectOption({ label: "Torre E2E" });
  await p.getByRole("button", { name: "Revisar archivo" }).click();
  await expect(p.getByText("Falta el nombre.")).toBeVisible();
  await expect(p.getByRole("button", { name: /Importar 2 contratos/ })).toBeDisabled();       // a row with an error blocks the import…
  await p.getByLabel(/Omitir las 1 filas/).check();                                            // …unless the person chooses to skip it
  await p.getByRole("button", { name: /Importar 2 contratos/ }).click();
  await expect(p.getByText(/Listo: 2 contratos creados/)).toBeVisible();

  // Doing it again creates nothing.
  await p.getByLabel("Archivo").setInputFiles(file);
  await p.getByRole("button", { name: "Revisar archivo" }).click();
  await expect(p.getByText(/0 por crear · 2 ya existentes/)).toBeVisible();

  // Names stay as written, name first.
  await p.goto("/tenants");
  await expect(p.getByText("Rosa Importada Prueba")).toBeVisible();

  // The imported lease shows its guarantor; give it a deposit, split into 3 installments.
  await p.goto("/leases");
  await p.getByRole("link", { name: /Rosa Importada Prueba/ }).first().click();
  await expect(p.getByText("Mario Aval")).toBeVisible();
  await p.getByRole("button", { name: "Editar", exact: true }).first().click();
  await p.getByLabel(/^Depósito/).fill("6500");
  await p.getByRole("button", { name: "Guardar cambios" }).click();
  await expect(p.getByText("Contrato actualizado.")).toBeVisible();
  await p.getByRole("button", { name: /Programar cobro/ }).click();
  await p.getByRole("button", { name: "3 abonos" }).click();
  await p.getByRole("button", { name: "Programar", exact: true }).click();
  await expect(p.getByText("Cobro del depósito programado.")).toBeVisible();
  await expect(p.getByText("Depósito (abono 3 de 3)").first()).toBeVisible();
  void d;
});

test("a deposit that is paid is held in custody and is not income", async ({ browser }) => {
  const org = await newOrganization(browser);
  const ana = await addTenant(browser, org, { name: "Ana Custodia Prueba", unit: "E2E-6", rent: 5000, chargeAmount: 100 });
  const dep = await api.post<{ installments: { chargeId: string }[] }>(org.owner.api, `/leases/${ana.leaseId}/deposit/schedule`, { installments: [{ dueDate: isoDate(0), amount: 5000 }] });
  await api.post(org.owner.api, "/payments", { tenantId: ana.tenantId, receivedOn: isoDate(0), amount: 5000, method: "BankTransfer", allocations: [{ chargeId: dep.installments[0].chargeId, amount: 5000 }] });

  const p = org.owner.page;
  await p.goto("/reports");
  await expect(p.getByText(/Depósitos en custodia/)).toBeVisible();
  await expect(p.getByText(/Depósitos en custodia \(no son ingreso\): \$5,000\.00/)).toBeVisible();
  const summary = await (await org.owner.api.get(`/api/v1/reports/financial-summary?from=${isoDate(-30)}&to=${isoDate(30)}`)).json();
  expect(summary.paymentsReceived.total).toBe(0);
  expect(summary.depositsHeld).toBe(5000);
});

test("a tenant cannot open the staff screens", async ({ browser }) => {
  const org = await newOrganization(browser);
  const ana = await addTenant(browser, org, { name: "Luis Acceso Prueba", unit: "E2E-7" });
  const p = ana.portal.page;
  for (const path of ["/settings/import", "/settings", "/reports", "/leases"]) {
    await p.goto(path);
    await expect(p).toHaveURL(/\/portal$/);                                         // sent back to their own home, never shown the screen
    await expect(p.getByText("Importar inquilinos y contratos")).toHaveCount(0);
  }
  expect((await ana.portal.api.post("/api/v1/imports/preview", { headers: { Origin: process.env.E2E_BASE_URL ?? "http://localhost:3101" }, multipart: { file: { name: "a.csv", mimeType: "text/csv", buffer: Buffer.from("x") } } })).status()).toBe(403);
});
