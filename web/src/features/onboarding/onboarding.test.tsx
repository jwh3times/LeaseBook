/**
 * Vitest component tests for M7 onboarding wizard (WP-5, Task 5.3).
 *
 * Three required assertions (from the brief):
 * (a) OnboardingChecklist renders correct step states from a mocked status.
 * (b) EntityImportStep renders the row-level error list from a mocked import response.
 * (c) VerificationStep sign-off button is disabled until isTied is true (and enabled when true).
 */
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import { EntityImportStep } from './ImportStep';
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
};

const STATUS_PARTIAL = {
  banksConfigured: true,
  entitiesImported: false,
  balancesImported: false,
  verified: false,
  signedOff: false,
  hasJournalData: false,
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

    // Continue is now enabled; clicking it advances explicitly.
    expect(continueBtn).toBeEnabled();
    await userEvent.click(continueBtn);
    expect(onContinue).toHaveBeenCalledTimes(1);
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

  it('disables sign-off button when isTied is false', async () => {
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.post('/api/onboarding/verification', () => HttpResponse.json(NOT_TIED_REPORT)),
    );

    render(withRouter(<VerificationStep />));

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

    render(withRouter(<VerificationStep />));

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
        }),
      ),
    );

    render(withRouter(<VerificationStep />));

    await userEvent.type(screen.getByLabelText('Owner equity total from AppFolio'), '5000');
    await userEvent.type(screen.getByLabelText('Deposit liability total from AppFolio'), '0');
    await userEvent.click(screen.getByRole('button', { name: /run verification/i }));

    const signoffBtn = await screen.findByRole('button', { name: /sign off migration/i });
    await userEvent.click(signoffBtn);

    expect(await screen.findByText(/migration verified and signed off/i)).toBeInTheDocument();
  });
});
