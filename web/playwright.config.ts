import { defineConfig } from "@playwright/test";

// End-to-end tests against a running stack (API + worker + web). `e2e/run.sh` starts one on a scratch database and runs this; see e2e/README.md.
// They use the system Chrome (no browser download) and create their own organization and people through the API, so they never depend on demo data.
export default defineConfig({
  testDir: "./e2e",
  testMatch: "**/*.spec.ts",
  timeout: 90_000,
  expect: { timeout: 20_000 },
  fullyParallel: true,
  workers: 3,
  retries: 0,
  reporter: [["list"]],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? "http://localhost:3101",
    channel: "chrome",
    headless: true,
    locale: "es-MX",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  outputDir: "./e2e/.results",
});
