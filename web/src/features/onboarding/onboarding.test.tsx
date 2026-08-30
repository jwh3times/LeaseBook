/**
 * Vitest component tests for M7 onboarding wizard (WP-5, Task 5.3).
 *
 * Three required assertions (from the brief):
 * (a) OnboardingChecklist renders correct step states from a mocked status.
 * (b) EntityImportStep renders the row-level error list from a mocked import response.
 * (c) VerificationStep sign-off button is disabled until isTied is true (and enabled when true).
 */
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { delay, http, HttpResponse } from 'msw';
import { useState } from 'react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import { BalanceImportStep, EntityImportStep } from './ImportStep';
import { OnboardingChecklist } from './OnboardingChecklist';
import { VerificationStep } from './VerificationStep';

// ─── Helpers ──────────────────────────────────────────────────────────────────

function makeQc() {
  return new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
}

function withRouter(
  element: React.ReactElement,
  extraRoutes: { path: string; element: React.ReactElement }[] = [],
) {
  const router = createMemoryRouter(
    [
      { path: '/', element },
      { path: '/settings', element: <div>settings page</div> },
      { path: '/banking', element: <div>banking page</div> },
      ...extraRoutes,
    ],
    { initialEntries: ['/'] },
  );
  return (
    <QueryClientProvider client={makeQc()}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  );
}

const STATUS_DONE = {
  banksConfigured: true,
  entitiesImported: true,
  balancesImported: true,
  verified: true,
  signedOff: true,
  hasJournalData: true,
  cutoverDate: '2026-01-31',
};

const STATUS_PARTIAL = {
  banksConfigured: true,
  entitiesImported: false,
  balancesImported: false,
  verified: false,
  signedOff: false,
  hasJournalData: false,
  cutoverDate: null,
};

// ─── (a) OnboardingChecklist step states ──────────────────────────────────────

describe('OnboardingChecklist', () => {
  it('shows all steps as complete when status is fully done', () => {
    render(
      withRouter(
        <OnboardingChecklist status={STATUS_DONE} activeStep={4} onSelectStep={() => undefined} />,
      ),
    );

    const list = screen.getByRole('list', { name: /onboarding steps/i });
    const items = within(list).getAllByRole('listitem');
    expect(items).toHaveLength(5);

    // Steps 1–4 (banks, entities, balances, verify) derive from backend flags and show "complete".
    // Step 5 (reconcile first month) has no backend flag, so it always shows "pending".
    for (let i = 0; i < 4; i++) {
      expect(items[i]!.getAttribute('aria-label')).toMatch(/complete/);
    }
    expect(items[4]!.getAttribute('aria-label')).toMatch(/pending/);
  });

  it('shows banks as complete, entities as pending/active when banks done but entities not', () => {
    render(
      withRouter(
        <OnboardingChecklist
          status={STATUS_PARTIAL}
          activeStep={1}
          onSelectStep={() => undefined}
        />,
      ),
    );

    const list = screen.getByRole('list', { name: /onboarding steps/i });
    const items = within(list).getAllByRole('listitem');

    expect(items[0]!.getAttribute('aria-label')).toMatch(/complete/);
    expect(items[1]!.getAttribute('aria-label')).toMatch(/pending/);
    expect(items[1]!.getAttribute('aria-current')).toBe('step');
  });

  it('marks step 0 (banks) not-current and step 1 as current when banks not done', () => {
    render(
      withRouter(
        <OnboardingChecklist
          status={{ ...STATUS_PARTIAL, banksConfigured: false }}
          activeStep={0}
          onSelectStep={() => undefined}
        />,
      ),
    );

    const list = screen.getByRole('list', { name: /onboarding steps/i });
    const items = within(list).getAllByRole('listitem');

    expect(items[0]!.getAttribute('aria-current')).toBe('step');
    expect(items[1]!.getAttribute('aria-current')).toBeNull();
  });
});

// ─── (b) EntityImportStep row-level errors ────────────────────────────────────

describe('EntityImportStep — row-level error list', () => {
  beforeEach(() => {
    document.body.innerHTML = '';
  });

  it('renders the row-level error table when the import response has errors', async () => {
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.post('/api/onboarding/import/:kind', () =>
        HttpResponse.json({
          batchId: 'b1',
          rowCount: 3,
          errorCount: 2,
          errors: [
            { rowNumber: 2, field: 'ownerName', reason: 'Required field missing' },
            { rowNumber: 5, field: 'email', reason: 'Invalid email format' },
          ],
        }),
      ),
    );

    render(
      withRouter(
        <EntityImportStep
          title="Import owners"
          description="Upload owner CSV"
          kinds={[{ kind: 'owners', label: 'Owners' }]}
        />,
      ),
    );

    // Simulate file upload
    const csv = 'name,email\n,not-email\n';
    const file = new File([csv], 'owners.csv', { type: 'text/csv' });
    const input = screen.getByLabelText('CSV file');
    await userEvent.upload(input, file);

    // Error table should appear
    const errorTable = await screen.findByRole('table', { name: /import errors/i });
    expect(errorTable).toBeInTheDocument();

    const rows = within(errorTable).getAllByRole('row');
    // 1 header + 2 error rows
    expect(rows).toHaveLength(3);

    expect(within(rows[1]!).getByText('2')).toBeInTheDocument();
    expect(within(rows[1]!).getByText('ownerName')).toBeInTheDocument();
    expect(within(rows[1]!).getByText('Required field missing')).toBeInTheDocument();

    expect(within(rows[2]!).getByText('5')).toBeInTheDocument();
    expect(within(rows[2]!).getByText('email')).toBeInTheDocument();
    expect(within(rows[2]!).getByText('Invalid email format')).toBeInTheDocument();
  });

  it('shows success banner when import has no errors', async () => {
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.post('/api/onboarding/import/:kind', () =>
        HttpResponse.json({
          batchId: 'b2',
          rowCount: 4,
          errorCount: 0,
          errors: [],
        }),
      ),
    );

    render(
      withRouter(
        <EntityImportStep
          title="Import properties"
          description="Upload property CSV"
          kinds={[{ kind: 'properties', label: 'Properties' }]}
        />,
      ),
    );

    const csv = 'address\n123 Main St\n';
    const file = new File([csv], 'props.csv', { type: 'text/csv' });
    const input = screen.getByLabelText('CSV file');
    await userEvent.upload(input, file);

    expect(await screen.findByText(/imported 4 rows successfully/i)).toBeInTheDocument();
    expect(screen.queryByRole('table', { name: /import errors/i })).not.toBeInTheDocument();
  });
});

// ─── (b2) EntityImportStep explicit advancement (Continue gating, no auto-advance) ─────

describe('EntityImportStep — explicit "Continue" advancement', () => {
  beforeEach(() => {
    document.body.innerHTML = '';
  });

  it('disables Continue until a kind is imported, keeps the step put after import, then enables it', async () => {
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.post('/api/onboarding/import/:kind', () =>
        HttpResponse.json({ batchId: 'b3', rowCount: 2, errorCount: 0, errors: [] }),
      ),
    );

    const onContinue = vi.fn();
    render(
      withRouter(
        <EntityImportStep
          title="Import entities"
          description="Upload CSVs"
          kinds={[
            { kind: 'owners', label: 'Owners' },
            { kind: 'properties', label: 'Properties' },
          ]}
          onContinue={onContinue}
        />,
      ),
    );

    // Continue is present but disabled before any import.
    const continueBtn = screen.getByRole('button', { name: /continue/i });
    expect(continueBtn).toBeDisabled();

    // Import owners → success banner shows; the step does NOT auto-advance (onContinue not called).
    const csv = 'Owner ID,Owner Name\nO1,Acme\n';
    const file = new File([csv], 'owners.csv', { type: 'text/csv' });
    await userEvent.upload(screen.getByLabelText('CSV file'), file);

    expect(await screen.findByText(/imported 2 rows successfully/i)).toBeInTheDocument();
    expect(onContinue).not.toHaveBeenCalled();

    // Per-upload state follows the selected kind, while completed-kind progress stays in the shell.
    await userEvent.click(screen.getByLabelText('Properties'));
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Owners').closest('label')?.querySelector('svg')).not.toBeNull();

    // Continue remains enabled across the kind switch; clicking it advances explicitly.
    expect(continueBtn).toBeEnabled();
    await userEvent.click(continueBtn);
    expect(onContinue).toHaveBeenCalledTimes(1);
  });
});

// ─── (b3) BalanceImportStep — corrected re-import (supersede) ────────────────

const BALANCE_KINDS: { kind: import('./onboarding').BalanceKind; label: string }[] = [
  { kind: 'owner_balances', label: 'Owner balances' },
  { kind: 'deposit_liabilities', label: 'Deposit liabilities' },
  { kind: 'bank_balances', label: 'Bank balances' },
  { kind: 'tenant_receivables', label: 'Tenant receivables' },
];

function renderBalanceStep() {
  render(
    withRouter(
      <BalanceImportStep
        title="Import balances"
        description="Upload balance CSVs"
        kinds={BALANCE_KINDS}
        establishedCutoverDate="2026-01-31"
      />,
    ),
  );
}

async function uploadCsv(csv: string) {
  const file = new File([csv], 'x.csv', { type: 'text/csv' });
  const input = screen.getByLabelText('CSV file');
  await userEvent.upload(input, file);
}

describe('BalanceImportStep — canonical cutover date', () => {
  test('requires an explicit date before the first balance upload', () => {
    render(
      withRouter(
        <BalanceImportStep
          title="Import balances"
          description="Upload balance CSVs"
          kinds={BALANCE_KINDS}
        />,
      ),
    );

    expect(screen.getByLabelText('Cutover date')).toHaveValue('');
    expect(screen.getByLabelText('Cutover date')).toBeRequired();
    expect(screen.getByLabelText('CSV file')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Upload CSV file' })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
  });

  test('renders an established cutover date as read-only', () => {
    render(
      withRouter(
        <BalanceImportStep
          title="Import balances"
          description="Upload balance CSVs"
          kinds={BALANCE_KINDS}
          establishedCutoverDate="2026-01-31"
        />,
      ),
    );

    expect(screen.getByLabelText('Cutover date')).toHaveValue('2026-01-31');
    expect(screen.getByLabelText('Cutover date')).toHaveAttribute('readonly');
  });

  test('adopts the canonical date when status establishes it after mount', async () => {
    function Harness() {
      const [established, setEstablished] = useState<string>();
      return (
        <>
          <button type="button" onClick={() => setEstablished('2026-01-31')}>
            Refresh status
          </button>
          <BalanceImportStep
            title="Import balances"
            description="Upload balance CSVs"
            kinds={BALANCE_KINDS}
            establishedCutoverDate={established}
          />
        </>
      );
    }

    render(withRouter(<Harness />));
    fireEvent.change(screen.getByLabelText('Cutover date'), {
      target: { value: '2026-02-28' },
    });

    await userEvent.click(screen.getByRole('button', { name: 'Refresh status' }));

    expect(screen.getByLabelText('Cutover date')).toHaveValue('2026-01-31');
    expect(screen.getByLabelText('Cutover date')).toHaveAttribute('readonly');
  });
});

describe('BalanceImportStep — corrected re-import (supersede)', () => {
  beforeEach(() => {
    document.body.innerHTML = '';
    server.use(http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })));
  });

  test('corrected re-import posts to the supersede endpoint and reports outcome counts', async () => {
    server.use(
      http.post('/api/onboarding/import-balances/owner_balances/supersede', () =>
        HttpResponse.json({
          batchId: 'b2',
          rowCount: 3,
          errorCount: 0,
          counts: {
            posted: 0,
            alreadyPosted: 0,
            unchanged: 2,
            superseded: 1,
            skipped: 0,
            errors: 0,
          },
          errors: [],
        }),
      ),
    );
    renderBalanceStep(); // the file's existing helper that mounts <BalanceImportStep …>
    await userEvent.click(screen.getByLabelText('This is a corrected re-import (supersede)'));
    await uploadCsv('Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,X,450.00,450.00\n'); // existing upload helper
    expect(await screen.findByText(/1 corrected, 2 unchanged/)).toBeInTheDocument();
  });

  test('supersede with no differing figures says so instead of a bare success', async () => {
    server.use(
      http.post('/api/onboarding/import-balances/owner_balances/supersede', () =>
        HttpResponse.json({
          batchId: 'b3',
          rowCount: 3,
          errorCount: 0,
          counts: {
            posted: 0,
            alreadyPosted: 0,
            unchanged: 3,
            superseded: 0,
            skipped: 0,
            errors: 0,
          },
          errors: [],
        }),
      ),
    );
    renderBalanceStep();
    await userEvent.click(screen.getByLabelText('This is a corrected re-import (supersede)'));
    await uploadCsv('Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,X,500.00,500.00\n');
    expect(
      await screen.findByText(/no figures differed — nothing was superseded/i),
    ).toBeInTheDocument();
  });

  // Regression: a corrected file that introduces a brand-new position (an owner missing from the
  // original import) comes back superseded=0 / posted=1. Keying the banner off `superseded` alone
  // rendered "nothing was superseded" straight after a genuine posting.
  test('supersede that adds a new position reports it instead of claiming nothing happened', async () => {
    server.use(
      http.post('/api/onboarding/import-balances/owner_balances/supersede', () =>
        HttpResponse.json({
          batchId: 'b4',
          rowCount: 3,
          errorCount: 0,
          counts: {
            posted: 1,
            alreadyPosted: 0,
            unchanged: 2,
            superseded: 0,
            skipped: 0,
            errors: 0,
          },
          errors: [],
        }),
      ),
    );
    renderBalanceStep();
    await userEvent.click(screen.getByLabelText('This is a corrected re-import (supersede)'));
    await uploadCsv(
      'Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-2,New Owner,300.00,300.00\n',
    );

    expect(await screen.findByText(/1 added, 2 unchanged/)).toBeInTheDocument();
    expect(screen.queryByText(/nothing was superseded/i)).not.toBeInTheDocument();
  });

  // Every non-zero bucket earns a clause — a mixed file must not drop the added/skipped rows.
  test('supersede banner lists every non-zero outcome bucket', async () => {
    server.use(
      http.post('/api/onboarding/import-balances/owner_balances/supersede', () =>
        HttpResponse.json({
          batchId: 'b5',
          rowCount: 6,
          errorCount: 0,
          counts: {
            posted: 1,
            alreadyPosted: 0,
            unchanged: 2,
            superseded: 2,
            skipped: 1,
            errors: 0,
          },
          errors: [],
        }),
      ),
    );
    renderBalanceStep();
    await userEvent.click(screen.getByLabelText('This is a corrected re-import (supersede)'));
    await uploadCsv('Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,X,450.00,450.00\n');

    expect(
      await screen.findByText(/2 corrected, 1 added, 2 unchanged, 1 skipped/),
    ).toBeInTheDocument();
  });
});

// ─── (b4) BalanceImportStep — result lifetime follows the selected mode ──────
//
// Regression coverage for the reviewer finding: the success banner used to key its content off
// the LIVE `supersede` checkbox rather than which mode actually produced the displayed
// result/counts, so toggling the checkbox after a result landed (without re-uploading)
// instantly relabeled a stale result under the wrong mode. The mode-keyed body now discards that
// stale result entirely, so there is nothing available to mislabel.

describe('BalanceImportStep — result lifetime follows the selected mode', () => {
  beforeEach(() => {
    document.body.innerHTML = '';
    server.use(http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })));
  });

  test('enabling supersede clears a plain-import result', async () => {
    server.use(
      http.post('/api/onboarding/import-balances/owner_balances', () =>
        HttpResponse.json({
          batchId: 'b4',
          rowCount: 3,
          errorCount: 0,
          // Nonzero counts on a *plain* import response: ImportBatchResult.counts is populated on
          // every response, plain or supersede. A mode change must discard this result rather than
          // reinterpret those counts as a corrected import.
          counts: {
            posted: 3,
            alreadyPosted: 0,
            unchanged: 5,
            superseded: 4,
            skipped: 0,
            errors: 0,
          },
          errors: [],
        }),
      ),
    );
    renderBalanceStep(); // supersede unchecked by default → hits the plain import endpoint
    await uploadCsv('Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,X,450.00,450.00\n');
    expect(await screen.findByText(/imported 3 rows successfully/i)).toBeInTheDocument();

    // Toggle supersede WITHOUT uploading again.
    await userEvent.click(screen.getByLabelText('This is a corrected re-import (supersede)'));

    expect(screen.queryByRole('status')).not.toBeInTheDocument();
    expect(screen.getByText(/drop a csv here or click to browse/i)).toBeInTheDocument();
    expect(screen.getByLabelText('This is a corrected re-import (supersede)')).toHaveFocus();
  });

  test('returning to plain import clears a supersede result', async () => {
    server.use(
      http.post('/api/onboarding/import-balances/owner_balances/supersede', () =>
        HttpResponse.json({
          batchId: 'b5',
          rowCount: 3,
          errorCount: 0,
          counts: {
            posted: 0,
            alreadyPosted: 0,
            unchanged: 2,
            superseded: 1,
            skipped: 0,
            errors: 0,
          },
          errors: [],
        }),
      ),
    );
    renderBalanceStep();
    await userEvent.click(screen.getByLabelText('This is a corrected re-import (supersede)'));
    await uploadCsv('Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,X,450.00,450.00\n');
    expect(await screen.findByText(/1 corrected, 2 unchanged/)).toBeInTheDocument();

    // Uncheck supersede WITHOUT uploading again.
    await userEvent.click(screen.getByLabelText('This is a corrected re-import (supersede)'));

    expect(screen.queryByRole('status')).not.toBeInTheDocument();
    expect(screen.getByText(/drop a csv here or click to browse/i)).toBeInTheDocument();
    expect(screen.getByLabelText('This is a corrected re-import (supersede)')).toHaveFocus();
  });
});

// ─── (b5) BalanceImportStep — stale mutation error/pending state on kind & mode switches ──
//
// Regression coverage for the reviewer finding: `mutation` (`useImportBalances(selectedKind)`)
// and `supersedeMutation` are single shared `useMutation` instances across all four balance
// kinds — switching `selectedKind` only changes which kind the *next* upload targets; it does not
// reset either mutation's own isError/error state, and `ApiErrorNotice` reads `activeMutation`
// live. Two consequences: (1) a failed upload for one kind kept showing its error notice after
// switching to a different, untouched kind (unambiguous misattribution, no timing required); (2)
// the supersede checkbox was enabled during isPending, so toggling it mid-flight (or after a
// failure, without re-uploading) changes which mutation instance the notice reads.

describe('BalanceImportStep — mutation error state resets on kind and mode switches', () => {
  beforeEach(() => {
    document.body.innerHTML = '';
    server.use(http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })));
  });

  test('switching balance kind clears a stale error notice', async () => {
    server.use(
      http.post('/api/onboarding/import-balances/owner_balances', () =>
        HttpResponse.json(
          { code: 'validation_failed', detail: 'The CSV could not be processed.' },
          { status: 400 },
        ),
      ),
    );
    renderBalanceStep(); // owner_balances is kinds[0] — selected by default
    await uploadCsv('Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,X,450.00,450.00\n');

    expect(await screen.findByText(/the csv could not be processed/i)).toBeInTheDocument();

    // Switch to a different kind WITHOUT re-uploading — the owner_balances failure must not
    // carry over and misattribute to deposit_liabilities.
    await userEvent.click(screen.getByLabelText('Deposit liabilities'));

    expect(screen.queryByText(/the csv could not be processed/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });

  test('switching balance kind preserves cutover date and completed-kind progress', async () => {
    server.use(
      http.post('/api/onboarding/import-balances/owner_balances', () =>
        HttpResponse.json({ batchId: 'b7', rowCount: 1, errorCount: 0, errors: [] }),
      ),
    );

    const onContinue = vi.fn();
    render(
      withRouter(
        <BalanceImportStep
          title="Import balances"
          description="Upload balance CSVs"
          kinds={BALANCE_KINDS}
          onContinue={onContinue}
        />,
      ),
    );

    const cutoverDate = screen.getByLabelText('Cutover date');
    fireEvent.change(cutoverDate, { target: { value: '2026-01-31' } });
    await uploadCsv('Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,X,450.00,450.00\n');

    expect(await screen.findByText(/imported 1 row successfully/i)).toBeInTheDocument();
    expect(
      screen.getByLabelText('Owner balances').closest('label')?.querySelector('svg'),
    ).not.toBeNull();
    expect(screen.getByRole('button', { name: /continue/i })).toBeEnabled();

    const supersede = screen.getByLabelText('This is a corrected re-import (supersede)');
    await userEvent.click(supersede);
    expect(screen.getByLabelText('This is a corrected re-import (supersede)')).toBeChecked();

    await userEvent.click(screen.getByLabelText('Deposit liabilities'));

    expect(cutoverDate).toHaveValue('2026-01-31');
    expect(
      screen.getByLabelText('Owner balances').closest('label')?.querySelector('svg'),
    ).not.toBeNull();
    expect(screen.getByRole('button', { name: /continue/i })).toBeEnabled();
    expect(screen.getByLabelText('This is a corrected re-import (supersede)')).not.toBeChecked();
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });

  test('supersede checkbox is disabled while a request is pending', async () => {
    server.use(
      http.post('/api/onboarding/import-balances/owner_balances', async () => {
        await delay('infinite');
        return HttpResponse.json({ batchId: 'b6', rowCount: 1, errorCount: 0, errors: [] });
      }),
    );
    renderBalanceStep();

    const checkbox = screen.getByLabelText('This is a corrected re-import (supersede)');
    expect(checkbox).toBeEnabled();

    await uploadCsv('Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,X,450.00,450.00\n');

    await waitFor(() => expect(checkbox).toBeDisabled());
  });
});

// ─── (b6) EntityImportStep — stale mutation error state on kind switch ──────
//
// The entity-side twin of (b5), and the reason this test exists at all: (b5) was found, fixed and
// pinned on the balance side while the identical defect stayed live here (#245). `EntityImportStep`
// renders its notice from `mutation.isError`, and `useImportEntities(selectedKind)` is a single
// shared observer across all four entity kinds — the kind switch reset `filename`/`result`/`errors`
// but not the mutation, so a failed upload kept its notice on screen attributed to a kind that was
// never uploaded. `OnboardingPage` renders this step with four kinds, so the radio group is live in
// production.
//
// The fix carries a second half this test does not assert directly: the twin also lacked the
// `.catch(() => null)` on `mutateAsync`, so a rejected import escaped as an unhandled rejection
// (handleFile is invoked fire-and-forget via `void handleChange(e)`). Asserting that needs Node's
// `process.on('unhandledRejection')`, and this tsconfig excludes Node globals on purpose — the
// scenario below is what exercises the rejecting path.

describe('EntityImportStep — mutation error state resets on kind switch', () => {
  beforeEach(() => {
    document.body.innerHTML = '';
    server.use(http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })));
  });

  test('switching entity kind clears a stale error notice', async () => {
    server.use(
      http.post('/api/onboarding/import/owners', () =>
        HttpResponse.json(
          { code: 'validation_failed', detail: 'The CSV could not be processed.' },
          { status: 400 },
        ),
      ),
    );
    render(
      withRouter(
        <EntityImportStep
          title="Import entities"
          description="Upload a CSV for each entity type"
          kinds={[
            { kind: 'owners', label: 'Owners' },
            { kind: 'properties', label: 'Properties' },
          ]}
        />,
      ),
    );

    const file = new File(['name,email\nX,x@example.com\n'], 'owners.csv', { type: 'text/csv' });
    await userEvent.upload(screen.getByLabelText('CSV file'), file);

    expect(await screen.findByText(/the csv could not be processed/i)).toBeInTheDocument();

    // Switch to a different kind WITHOUT re-uploading — the owners failure must not carry over
    // and misattribute to properties.
    await userEvent.click(screen.getByLabelText('Properties'));

    expect(screen.queryByText(/the csv could not be processed/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });
});

// ─── (c) VerificationStep sign-off button ────────────────────────────────────

describe('VerificationStep sign-off button', () => {
  beforeEach(() => {
    document.body.innerHTML = '';
  });

  const NOT_TIED_REPORT = {
    verificationId: 'v1',
    cutoverDate: '2026-01-31',
    isTied: false,
    varianceTotal: 150.0,
    clearingCash: 150.0,
    clearingAccrual: 0.0,
    reportSnapshot: '{}',
    lines: [
      { key: 'ownerEquity', label: 'Owner equity', expected: 5000, actual: 4850, variance: 150 },
    ],
  };

  const TIED_REPORT = {
    verificationId: 'v2',
    cutoverDate: '2026-01-31',
    isTied: true,
    varianceTotal: 0.0,
    clearingCash: 0.0,
    clearingAccrual: 0.0,
    reportSnapshot: '{}',
    lines: [
      { key: 'ownerEquity', label: 'Owner equity', expected: 5000, actual: 5000, variance: 0 },
    ],
  };

  it('requires the canonical cutover date before verification can run', () => {
    render(withRouter(<VerificationStep />));

    expect(screen.getByLabelText('Cutover date')).toHaveValue('');
    expect(screen.getByLabelText('Cutover date')).toBeRequired();
    expect(screen.getByRole('button', { name: /run verification/i })).toBeDisabled();
  });

  it('adopts the canonical date when status establishes it after mount', async () => {
    function Harness() {
      const [established, setEstablished] = useState<string>();
      return (
        <>
          <button type="button" onClick={() => setEstablished('2026-01-31')}>
            Refresh status
          </button>
          <VerificationStep cutoverDate={established} />
        </>
      );
    }

    render(withRouter(<Harness />));
    await userEvent.click(screen.getByRole('button', { name: 'Refresh status' }));

    expect(screen.getByLabelText('Cutover date')).toHaveValue('2026-01-31');
    expect(screen.getByLabelText('Cutover date')).toHaveAttribute('readonly');
    expect(screen.getByRole('button', { name: /run verification/i })).toBeEnabled();
  });

  it('disables sign-off button when isTied is false', async () => {
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.post('/api/onboarding/verification', () => HttpResponse.json(NOT_TIED_REPORT)),
    );

    render(withRouter(<VerificationStep cutoverDate="2026-01-31" />));

    // Fill in minimal form and run verification
    await userEvent.type(screen.getByLabelText('Owner equity total from AppFolio'), '4850');
    await userEvent.type(screen.getByLabelText('Deposit liability total from AppFolio'), '0');
    await userEvent.click(screen.getByRole('button', { name: /run verification/i }));

    const signoffBtn = await screen.findByRole('button', { name: /sign off migration/i });
    expect(signoffBtn).toBeDisabled();
    // Also check the helper text
    expect(screen.getByText(/sign-off is disabled until the import ties/i)).toBeInTheDocument();
  });

  it('enables sign-off button when isTied is true', async () => {
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.post('/api/onboarding/verification', () => HttpResponse.json(TIED_REPORT)),
    );

    render(withRouter(<VerificationStep cutoverDate="2026-01-31" />));

    await userEvent.type(screen.getByLabelText('Owner equity total from AppFolio'), '5000');
    await userEvent.type(screen.getByLabelText('Deposit liability total from AppFolio'), '0');
    await userEvent.click(screen.getByRole('button', { name: /run verification/i }));

    const signoffBtn = await screen.findByRole('button', { name: /sign off migration/i });
    expect(signoffBtn).not.toBeDisabled();
    expect(screen.queryByText(/sign-off is disabled/i)).not.toBeInTheDocument();
  });

  it('signs off and shows success message when tied and sign-off succeeds', async () => {
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.post('/api/onboarding/verification', () => HttpResponse.json(TIED_REPORT)),
      http.post(
        '/api/onboarding/verification/:id/signoff',
        () => new HttpResponse(null, { status: 200 }),
      ),
      http.get('/api/onboarding/status', () =>
        HttpResponse.json({
          banksConfigured: true,
          entitiesImported: true,
          balancesImported: true,
          verified: true,
          signedOff: true,
          hasJournalData: true,
          cutoverDate: '2026-01-31',
        }),
      ),
    );

    render(withRouter(<VerificationStep cutoverDate="2026-01-31" />));

    await userEvent.type(screen.getByLabelText('Owner equity total from AppFolio'), '5000');
    await userEvent.type(screen.getByLabelText('Deposit liability total from AppFolio'), '0');
    await userEvent.click(screen.getByRole('button', { name: /run verification/i }));

    const signoffBtn = await screen.findByRole('button', { name: /sign off migration/i });
    await userEvent.click(signoffBtn);

    expect(await screen.findByText(/migration verified and signed off/i)).toBeInTheDocument();
  });

  it('renders its own copy for not_tied rather than the raw server message', async () => {
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.post('/api/onboarding/verification', () => HttpResponse.json(TIED_REPORT)),
      http.post('/api/onboarding/verification/:id/signoff', () =>
        HttpResponse.json(
          {
            code: 'not_tied',
            title: 'not_tied',
            detail: 'Verification 0193ab7c-dead-beef no longer ties.',
            correlationId: 'abc123def456',
          },
          { status: 409 },
        ),
      ),
      http.get('/api/onboarding/status', () =>
        HttpResponse.json({
          banksConfigured: true,
          entitiesImported: true,
          balancesImported: true,
          verified: true,
          signedOff: true,
          hasJournalData: true,
          cutoverDate: '2026-01-31',
        }),
      ),
    );

    render(withRouter(<VerificationStep cutoverDate="2026-01-31" />));

    await userEvent.type(screen.getByLabelText('Owner equity total from AppFolio'), '5000');
    await userEvent.type(screen.getByLabelText('Deposit liability total from AppFolio'), '0');
    await userEvent.click(screen.getByRole('button', { name: /run verification/i }));

    const signoffBtn = await screen.findByRole('button', { name: /sign off migration/i });
    await userEvent.click(signoffBtn);

    // The component's own copy (VerificationStep.tsx:96-100) — NOT the server detail. Note the
    // component's copy says "Correct and re-import before signing off." — asserting the *server's*
    // rewritten text here would invert the test's purpose.
    expect(
      await screen.findByText(/correct and re-import before signing off/i),
    ).toBeInTheDocument();
    expect(screen.queryByText(/0193ab7c-dead-beef/)).not.toBeInTheDocument();
  });
});

// ─── (d) VerificationStep held-fees attestation field (WP-7 Task 13) ──────────
//
// D5 (fiduciary): the held-fees field is a first-class "unattested" input. A BLANK field must send
// heldPmFeesTotal: null in the request body — absence ≠ zero, so blank must NEVER be coerced to 0.
// A filled field sends the parsed number. The variance line then renders automatically from
// report.lines (no bespoke rendering), so these tests assert only the request-body contract.

describe('VerificationStep held-fees attestation field', () => {
  beforeEach(() => {
    document.body.innerHTML = '';
  });

  const HELD_REPORT = {
    verificationId: 'v-held',
    cutoverDate: '2026-01-31',
    isTied: true,
    varianceTotal: 0.0,
    clearingCash: 0.0,
    clearingAccrual: 0.0,
    reportSnapshot: '{}',
    lines: [],
  };

  it('sends heldPmFeesTotal: null when the held-fees field is left blank (absence ≠ zero)', async () => {
    let verifyBody: Record<string, unknown> | undefined;
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.post('/api/onboarding/verification', async ({ request }) => {
        verifyBody = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json(HELD_REPORT);
      }),
    );

    render(withRouter(<VerificationStep cutoverDate="2026-01-31" />));

    await userEvent.type(screen.getByLabelText('Owner equity total from AppFolio'), '500');
    await userEvent.type(screen.getByLabelText('Deposit liability total from AppFolio'), '500');
    // Held-fees field deliberately left BLANK.
    await userEvent.click(screen.getByRole('button', { name: /run verification/i }));

    await waitFor(() => expect(verifyBody).toBeDefined());
    // The property is PRESENT and null — not omitted, not 0.
    expect(verifyBody).toHaveProperty('heldPmFeesTotal', null);
  });

  it('sends the parsed number when the held-fees field is filled', async () => {
    let verifyBody: Record<string, unknown> | undefined;
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.post('/api/onboarding/verification', async ({ request }) => {
        verifyBody = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json(HELD_REPORT);
      }),
    );

    render(withRouter(<VerificationStep cutoverDate="2026-01-31" />));

    await userEvent.type(screen.getByLabelText('Owner equity total from AppFolio'), '500');
    await userEvent.type(screen.getByLabelText('Deposit liability total from AppFolio'), '500');
    await userEvent.type(screen.getByLabelText('Held PM fees total from AppFolio'), '100');
    await userEvent.click(screen.getByRole('button', { name: /run verification/i }));

    await waitFor(() => expect(verifyBody).toBeDefined());
    expect(verifyBody).toHaveProperty('heldPmFeesTotal', 100);
  });
});
