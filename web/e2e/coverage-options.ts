import path from 'node:path';
import type { CoverageReportOptions } from 'monocart-coverage-reports';

// Shared by the recording fixture (`coverage-fixture.ts`, which adds each test's raw V8 coverage to
// the cache under `outputDir`) and by `scripts/e2e-coverage.mjs`, which generates the report from
// that cache once every pass has run, which also clears it. Both must name the same `outputDir`.

const WEB_ROOT = path.resolve(import.meta.dirname, '..');

/** Everything the coverage run writes: `web/coverage/e2e/`. */
export const COVERAGE_DIR = path.join(WEB_ROOT, 'coverage', 'e2e');

// The same source set the unit figures use (`coverage.include`/`exclude` in vite.config.ts), so the
// two web reports have the same denominator. Takes a path relative to `web/`.
const isMeasuredSource = (relativePath: string) =>
  /^src\/.+\.(ts|tsx)$/.test(relativePath) &&
  !relativePath.startsWith('src/api/generated/') &&
  !relativePath.startsWith('src/test/') &&
  !/\.(test|spec)\.(ts|tsx)$/.test(relativePath) &&
  !relativePath.endsWith('.d.ts');

// `/abs/web/src/app/router.tsx` and `localhost-5373/src/app/router.tsx` → `src/app/router.tsx`.
const fromSrc = (anyPath: string) =>
  anyPath.replaceAll('\\', '/').replace(/^(?:.*\/)?(?=src\/)/, '');

export const webCoverageOptions: CoverageReportOptions = {
  name: 'Web coverage as reached by the browser',
  outputDir: path.join(COVERAGE_DIR, 'web'),
  // The Vite dev server serves one module per source file, at that file's path. This is where the
  // source set is decided: dependencies (`/node_modules/`, `/@vite`, `/@react-refresh`) and the
  // stylesheets Vite serves as modules are not LeaseBook's TypeScript.
  entryFilter: (entry) => isMeasuredSource(new URL(entry.url).pathname.slice(1)),
  // The dev server's source maps name each source by its file name alone (`router.tsx`), so a
  // source cannot be told from another by directory here; the entry filter above already did that.
  sourceFilter: (sourcePath) => /\.(ts|tsx)$/.test(sourcePath),
  // For the same reason, a source is reported under the path of the module it was unpacked from.
  sourcePath: (filePath, info) => fromSrc(info.distFile ?? filePath),
  // Files no page loaded count as uncovered instead of going missing.
  all: {
    dir: [path.join(WEB_ROOT, 'src')],
    filter: (filePath) => isMeasuredSource(fromSrc(filePath)),
  },
  // Istanbul-format reports throughout, so the HTML report, the Cobertura file and the summary table
  // all count lines the same way.
  reports: [['html', { subdir: 'html' }], ['cobertura'], ['json-summary']],
};
