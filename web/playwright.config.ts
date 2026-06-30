import { defineConfig, devices } from '@playwright/test';

// e2e for the budgeted M2 flows (§D step 6). Runs the SPA via the Vite dev server (which proxies
// /api → :5080) against the .NET host on :5080. The host must point at a Postgres seeded with the demo
// org (`./scripts/dev.ps1 up` + `seed --org demo`) — the specs sign in as the seeded admin.
export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  // Serial, single worker: the specs share one seeded demo org, and the M4 reconcile spec finalizes a
  // per-account-month lock — persistent state that would race other specs posting into that month if files
  // ran on parallel workers. One worker runs files in discovery order (budgeted → m3 → m4 → smoke), so the
  // M3 June payment posts+voids before the M4 spec locks June.
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  reporter: 'html',
  use: {
    baseURL: 'http://localhost:5373',
    trace: 'on-first-retry',
  },
  webServer: [
    {
      command:
        'dotnet run --project ../src/LeaseBook.Web --no-launch-profile --urls http://localhost:5080',
      url: 'http://localhost:5080/api/health',
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        // In CI, ConnectionStrings__Default points the host at the Postgres service container
        // (port 5432, app role). Unset locally → host uses appsettings.Development.json (5632).
        ...(process.env.ConnectionStrings__Default
          ? { ConnectionStrings__Default: process.env.ConnectionStrings__Default }
          : {}),
      },
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
