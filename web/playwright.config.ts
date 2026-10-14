import { randomBytes } from 'node:crypto';
import { defineConfig, devices } from '@playwright/test';

// e2e for the budgeted M2 flows (§D step 6). Runs the SPA via the Vite dev server (which proxies
// /api → :5080) against the .NET host on :5080. The host must point at a Postgres seeded with the demo
// org (`./scripts/dev.ps1 up` + `seed --org demo`) — the specs sign in as the seeded admin.
//
// Coverage run only (`npm run e2e:coverage`, ADR-055): with E2E_COVERAGE=1 the host starts as a
// client of the `dotnet-coverage` session that `scripts/e2e-coverage.mjs` opened, and is stopped
// with SIGTERM so that it exits the way a deployed host does and its shutdown path is counted too.
// (The collector kept a SIGKILLed host's counters when that was tried, but nothing documents it.)
// Unset, both values below are what they were before the coverage run existed: the plain
// `dotnet run` command, and Playwright's default shutdown.
const HOST_COMMAND =
  'dotnet run --project ../src/LeaseBook.Web --no-launch-profile --urls http://localhost:5080';
const coverageSession =
  process.env.E2E_COVERAGE === '1' ? process.env.E2E_COVERAGE_SESSION : undefined;

// The Stripe stand-in pass only (STRIPE_STAND_IN_E2E=1, ADR-054): the host runs in `StripeSandbox`
// mode against `scripts/stripe-stand-in.mjs` on loopback, and makes no call to Stripe. The stand-in
// starts first, because the host asks whose key it holds before it serves. The key is the one fixed
// key a host may send a stand-in. The webhook signing secret is made here for each run and is never
// written down: the stand-in signs with it and the host verifies with it. All three are set on this
// process, so both servers and the test's own `payment-simulation` commands read the same values; a
// worker inherits them, and so does not make a second secret when it loads this file again.
// `Payments__ManifestPath` and `ConnectionStrings__Default` name the stand-in fixture
// (`scripts/payment-fixture.ps1 init -StandIn`) and are the caller's to set.
const standIn = process.env.STRIPE_STAND_IN_E2E === '1';
if (standIn) {
  process.env.Payments__Stripe__ApiBase ??= 'http://127.0.0.1:4010';
  process.env.Payments__Stripe__SecretKey = 'sk_test_leasebook_stand_in';
  process.env.Payments__Stripe__WebhookSecret ??= `whsec_${randomBytes(24).toString('hex')}`;
  // The host's HTTP client takes a proxy from the environment, loopback included: with `http_proxy`,
  // `HTTP_PROXY` or `ALL_PROXY` set, its requests to the stand-in go to that proxy and the host does
  // not start. Naming loopback here keeps them on this machine. Found by trying each on 2026-10-13.
  for (const name of ['NO_PROXY', 'no_proxy']) {
    const listed = (process.env[name] ?? '').split(',').filter(Boolean);
    process.env[name] = [...new Set([...listed, '127.0.0.1', 'localhost'])].join(',');
  }
}

export default defineConfig({
  testDir: './e2e',
  expect: {
    toHaveScreenshot: { maxDiffPixelRatio: 0.02, animations: 'disabled' },
  },
  // Baselines live under web/e2e-snapshots/ (relative to this config's dir), suffixed by project+platform.
  snapshotPathTemplate: 'e2e-snapshots/{testFileName}/{arg}-{projectName}-{platform}{ext}',
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
    ...(standIn
      ? [
          {
            command: 'node ../scripts/stripe-stand-in.mjs',
            url: `${process.env.Payments__Stripe__ApiBase}/stand-in/health`,
            // Its state is one run's, and it signs with this run's secret: never one left running.
            reuseExistingServer: false,
            // Every request it answers and every delivery it sends, in the run's own output.
            stdout: 'pipe' as const,
            timeout: 30_000,
          },
        ]
      : []),
    {
      command: coverageSession
        ? `dotnet dotnet-coverage connect ${coverageSession} ${HOST_COMMAND}`
        : HOST_COMMAND,
      ...(coverageSession
        ? { gracefulShutdown: { signal: 'SIGTERM' as const, timeout: 60_000 } }
        : {}),
      url: 'http://localhost:5080/api/health',
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        // In CI, ConnectionStrings__Default points the host at the Postgres service container
        // (port 5432, app role). Unset locally → host uses appsettings.Development.json (5632).
        ...(process.env.ConnectionStrings__Default
          ? { ConnectionStrings__Default: process.env.ConnectionStrings__Default }
          : {}),
      },
      // A host that is already running is not under the collector, so the coverage run never reuses one.
      reuseExistingServer:
        !process.env.CI &&
        process.env.PAYMENT_SIMULATION_E2E !== '1' &&
        !standIn &&
        !coverageSession,
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
