import { createHash } from 'node:crypto';
import { appendFileSync, mkdirSync } from 'node:fs';
import path from 'node:path';
import type {
  APIRequestContext,
  BrowserContext,
  Page,
  Response,
  test as playwrightTest,
} from '@playwright/test';
import { COVERAGE_DIR, webCoverageOptions } from './coverage-options';

// What the coverage run records (ADR-055, docs/testing/coverage.md). Only `./test.ts` imports this,
// and it calls `withCoverage` only when E2E_COVERAGE=1. Loading this module does nothing by itself:
// its Playwright imports are types, and the report library is imported inside the fixture.
//
// Two recordings per test, both written under web/coverage/e2e/:
//
//   - Chromium's V8 JavaScript coverage for every page the test opened, handed to
//     monocart-coverage-reports, which keeps it on disk until the run ends and `e2e-coverage.mjs`
//     turns it into a per-source-file report through the Vite dev server's source maps.
//   - The method and path of every API request, one JSON object per line, which
//     `scripts/route-inventory.mjs` matches against the API contract's route templates.

const REQUESTS_DIR = path.join(COVERAGE_DIR, 'requests');

// A request is recorded when its path is one the host serves as an endpoint, not as the SPA or a
// static file. `/openapi` is the development-only contract endpoint.
const API_PATH = /^\/(api|callbacks|openapi)(\/|$)/;

type Via = 'browser' | 'api-client';
type RecordedRequest = { method: string; path: string; via: Via; status: number; served: boolean };

export const withCoverage = (base: typeof playwrightTest): typeof playwrightTest =>
  base.extend<{ recordCoverage: void }>({
    recordCoverage: [
      async ({ browser, request }, use, testInfo) => {
        const records: RecordedRequest[] = [];
        const pending: Promise<void>[] = [];
        const pages = new Set<Page>();
        const collected = new WeakSet<Page>();
        const { default: MCR } = await import('monocart-coverage-reports');
        const report = MCR(webCoverageOptions);

        const record = (method: string, url: string, via: Via, status: number, served: boolean) => {
          const { pathname } = new URL(url, 'http://localhost');
          if (API_PATH.test(pathname))
            records.push({ method, path: pathname, via, status, served });
        };

        // A response the test fulfilled with `page.route` never touched the network, so it has no
        // server address. That is the difference between a route the host served and one a test
        // answered for it, and only the first counts as reached.
        const onResponse = (response: Response) => {
          pending.push(
            response
              .serverAddr()
              .catch(() => null)
              .then((address) => {
                record(
                  response.request().method(),
                  response.url(),
                  'browser',
                  response.status(),
                  address !== null,
                );
              }),
          );
        };

        // Every helper on the request client (`get`, `post`, ...) goes through `fetch`.
        const recordClient = (client: APIRequestContext) => {
          const fetch = client.fetch.bind(client);
          client.fetch = async (urlOrRequest, options) => {
            const response = await fetch(urlOrRequest, options);
            const method =
              options?.method ?? (typeof urlOrRequest === 'string' ? 'GET' : urlOrRequest.method());
            record(method.toUpperCase(), response.url(), 'api-client', response.status(), true);
            return response;
          };
        };

        const startPage = (page: Page) => {
          pages.add(page);
          // Sent before the test's first navigation on this page: the `page` event is delivered
          // before `newPage()` returns. `resetOnNavigation: false` keeps what ran before each reload.
          pending.push(page.coverage.startJSCoverage({ resetOnNavigation: false }).catch(() => {}));
        };

        const collectPage = async (page: Page) => {
          if (collected.has(page) || page.isClosed()) return;
          collected.add(page);
          const entries = await page.coverage.stopJSCoverage().catch(() => []);
          if (entries.length > 0) await report.add(entries);
        };

        const track = (context: BrowserContext) => {
          context.on('page', startPage);
          context.on('response', onResponse);
          recordClient(context.request);
          // Coverage has to be read while the pages are still open, and the built-in `context`
          // fixture closes its context before this fixture's teardown runs.
          const close = context.close.bind(context);
          context.close = async (options) => {
            await Promise.all(context.pages().map(collectPage));
            await close(options);
          };
        };

        // The built-in `context` fixture and a test's own `browser.newContext()` both arrive here.
        const newContext = browser.newContext.bind(browser);
        browser.newContext = async (options) => {
          const context = await newContext(options);
          track(context);
          return context;
        };
        recordClient(request);

        try {
          await use();
        } finally {
          browser.newContext = newContext;
          await Promise.all([...pages].map(collectPage));
          await Promise.all(pending);
          if (records.length > 0) {
            mkdirSync(REQUESTS_DIR, { recursive: true });
            // One file per test attempt: a retry appends its own file and never rewrites another's.
            const id = createHash('sha256')
              .update(`${testInfo.testId}:${testInfo.retry}:${testInfo.repeatEachIndex}`)
              .digest('hex')
              .slice(0, 16);
            appendFileSync(
              path.join(REQUESTS_DIR, `${process.env.E2E_COVERAGE_PASS ?? 'run'}-${id}.ndjson`),
              records.map((entry) => JSON.stringify(entry)).join('\n') + '\n',
            );
          }
        }
      },
      { auto: true },
    ],
  });
