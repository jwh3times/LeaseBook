import { defineConfig, devices } from '@playwright/test';

// e2e for the budgeted M2 flows (§D step 6). Runs the SPA via the Vite dev server (which proxies
// /api → :5080) against the .NET host on :5080. The host must point at a Postgres seeded with the demo
// org (`./scripts/dev.ps1 up` + `seed --org demo`) — the specs sign in as the seeded admin.
export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  reporter: 'html',
  use: {
    baseURL: 'http://localhost:5373',
    trace: 'on-first-retry',
  },
  webServer: [
    {
      command: 'dotnet run --project ../src/LeaseBook.Web --no-launch-profile --urls http://localhost:5080',
      url: 'http://localhost:5080/api/health',
      env: { ASPNETCORE_ENVIRONMENT: 'Development' },
      reuseExistingServer: true,
      timeout: 180_000,
    },
    {
      command: 'npm run dev',
      url: 'http://localhost:5373',
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
    },
  ],
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
