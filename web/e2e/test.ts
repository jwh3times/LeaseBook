import { test as base } from '@playwright/test';
import { withCoverage } from './coverage-fixture';

// Every spec takes `test` from here and not from '@playwright/test'.
//
// Unless E2E_COVERAGE=1, this IS Playwright's own `test`: the same object, with no fixture added,
// so `npm run e2e` runs exactly as it would importing it directly. With E2E_COVERAGE=1 (set by
// `npm run e2e:coverage`, never by the pull-request e2e job) it is the same `test` extended with one
// automatic fixture that records browser coverage and the API requests each test makes (ADR-055).
//
// A spec that imports `test` from '@playwright/test' still runs, but its coverage and requests go
// unrecorded. `scripts/e2e-coverage.mjs` refuses to start while one does.
export const test: typeof base = process.env.E2E_COVERAGE === '1' ? withCoverage(base) : base;
