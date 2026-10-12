import MCR from 'monocart-coverage-reports';
import { webCoverageOptions } from './coverage-options.ts';

// Generates the browser coverage report from what the fixture cached during the run. Started by
// `scripts/e2e-coverage.mjs` once every Playwright pass has finished; Node runs it as it stands.

const report = MCR(webCoverageOptions);

// No cache means no test recorded anything: the fixture was not installed or E2E_COVERAGE was not
// set. A report built from nothing would show every file at 0% and pass for a figure.
if (!report.hasCache()) {
  console.error('No browser coverage was recorded. Was the e2e run started with E2E_COVERAGE=1?');
  process.exit(1);
}

const results = await report.generate();
if (!results) {
  console.error('The browser coverage report could not be generated.');
  process.exit(1);
}
