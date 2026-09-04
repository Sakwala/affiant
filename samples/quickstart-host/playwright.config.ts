import { defineConfig, devices } from "@playwright/test";

/**
 * The deck runs against a host you started yourself (see README.md), not one Playwright launches:
 * the host owns two SQLite files and a background expiry sweep, and starting it here would hide
 * both behind a test runner. Point BASE_URL at it.
 *
 * One worker, no parallelism. The specs share a docket and a database, and two of them wait on the
 * framework's own 30-second expiry sweep — running them concurrently would have them racing each
 * other's cards for no gain.
 */
export default defineConfig({
  testDir: "./e2e",
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: 0,
  reporter: process.env.CI ? [["list"], ["html", { open: "never" }]] : [["list"]],
  timeout: 60_000,
  expect: { timeout: 10_000 },
  use: {
    baseURL: process.env.BASE_URL ?? "http://localhost:5077",
    trace: "retain-on-failure",
    video: "off",
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
});
