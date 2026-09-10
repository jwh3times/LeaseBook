import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { beforeEach, describe, expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { DashboardPage } from './DashboardPage';

const DASH = {
  kpis: {
    trustTotal: 445380.14,
    availableToDisburse: 103010.01,
    uncleared: 250.0,
    unclearedCount: 3,
    tenantPaymentsMtd: 1380,
    scheduledRent: 10855,
    vacancy: 13,
  },
  ownerBalances: {
    rows: [
      {
        ownerId: 'o1',
        name: 'Hargrove Family Trust',
        operating: 14820.5,
        deposits: 8400,
        total: 23220.5,
        isRollup: false,
      },
      {
        ownerId: 'agg',
        name: 'All other owners',
        operating: 140147.74,
        deposits: 0,
        total: 140147.74,
        isRollup: true,
      },
    ],
    totals: { operating: 154968.24, deposits: 8400, total: 163368.24 },
  },
  banks: {
    rows: [
      { bankAccountId: 'b1', name: 'Operating Trust', book: 248930.14, unclearedCount: 3 },
      { bankAccountId: 'b2', name: 'Security Deposit Trust', book: 196450, unclearedCount: 0 },
    ],
  },
  actionItems: [
    {
      id: 'a1',
      kind: 'info',
      title: 'Deposits awaiting application',
      detail: '10 held deposits',
      route: '/banking',
    },
  ],
};

/** Operational org: has journal data and is signed off — no banner, no redirect. */
const OB = {
  hasJournalData: true,
  signedOff: true,
  entitiesImported: false,
  balancesImported: false,
  verified: false,
};

function renderDashboard() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      { path: '/dashboard', element: <DashboardPage /> },
      { path: '/owners/:id', element: <div>owner page</div> },
      { path: '/banking', element: <div>banking page</div> },
      { path: '/onboarding', element: <div>onboarding page</div> },
    ],
    { initialEntries: ['/dashboard'] },
  );
  render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

describe('DashboardPage', () => {
  it('renders the KPIs, the named owner hero with the roll-up, and the bank summary', async () => {
    server.use(
      http.get('/api/dashboard', () => HttpResponse.json(DASH)),
      http.get('/api/onboarding/status', () => HttpResponse.json(OB)),
    );
    renderDashboard();

    expect(await screen.findByText('Trust total')).toBeInTheDocument();
    expect(screen.getByText('Available to disburse')).toBeInTheDocument();
    expect(screen.getByText('Tenant payments received')).toBeInTheDocument();
    expect(screen.getByText(/scheduled rent/i)).toHaveTextContent('billing baseline');
    expect(screen.getByText(/445,380\.14/)).toBeInTheDocument();

    // The hero is named (0-click owner balances) and shows the relabeled roll-up.
    expect(screen.getByText('Hargrove Family Trust')).toBeInTheDocument();
    expect(screen.getByText('All other owners')).toBeInTheDocument();

    // Trust accounts and needs-attention items render.
    expect(screen.getByText('Operating Trust')).toBeInTheDocument();
    expect(screen.queryByText('PM Operating')).not.toBeInTheDocument();
    expect(screen.getByText('Deposits awaiting application')).toBeInTheDocument();

    // Per-account uncleared count renders on the bank summary card.
    expect(screen.getByText('3 uncleared')).toBeInTheDocument();
    expect(screen.getAllByText('Reconciled')).toHaveLength(1);

    // The "Uncleared" StatCard shows the non-zero count badge.
    expect(screen.getByText('3 items')).toBeInTheDocument();
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
  });

  it('navigates to an owner from the hero', async () => {
    server.use(
      http.get('/api/dashboard', () => HttpResponse.json(DASH)),
      http.get('/api/onboarding/status', () => HttpResponse.json(OB)),
    );
    renderDashboard();
    await userEvent.click(await screen.findByText('Hargrove Family Trust'));
    expect(await screen.findByText('owner page')).toBeInTheDocument();
  });

  it('deep-links an action item to its route', async () => {
    server.use(
      http.get('/api/dashboard', () => HttpResponse.json(DASH)),
      http.get('/api/onboarding/status', () => HttpResponse.json(OB)),
    );
    renderDashboard();
    await userEvent.click(await screen.findByText('Deposits awaiting application'));
    expect(await screen.findByText('banking page')).toBeInTheDocument();
  });

  it('shows an error state when the dashboard fails', async () => {
    server.use(
      http.get('/api/dashboard', () => new HttpResponse(null, { status: 500 })),
      http.get('/api/onboarding/status', () => HttpResponse.json(OB)),
    );
    renderDashboard();
    expect(await screen.findByText(/couldn't load the dashboard/i)).toBeInTheDocument();
  });

  it('carries the support reference and retries the dashboard in place', async () => {
    const reference = '5a5b5c5d5a5b5c5d5a5b5c5d5a5b5c5d';
    let attempt = 0;
    server.use(
      http.get('/api/dashboard', () => {
        attempt += 1;
        return attempt === 1
          ? HttpResponse.json(
              { detail: 'The dashboard projection is rebuilding.', correlationId: reference },
              { status: 503 },
            )
          : HttpResponse.json(DASH);
      }),
      http.get('/api/onboarding/status', () => HttpResponse.json(OB)),
    );
    renderDashboard();

    expect(await screen.findByText(/couldn't load the dashboard/i)).toBeInTheDocument();
    expect(screen.getByText('The dashboard projection is rebuilding.')).toBeInTheDocument();
    expect(screen.getByText(`Reference: ${reference}`)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));

    expect(await screen.findByText('Trust total')).toBeInTheDocument();
  });

  beforeEach(() => {
    document.body.innerHTML = '';
  });
});

describe('DashboardPage when the onboarding status is unavailable', () => {
  it('says the status is unavailable instead of rendering as a completed org', async () => {
    server.use(
      http.get('/api/dashboard', () => HttpResponse.json(DASH)),
      http.get('/api/onboarding/status', () => new HttpResponse(null, { status: 503 })),
    );
    renderDashboard();

    // The dashboard itself still works — the onboarding read is auxiliary.
    expect(await screen.findByText('Trust total')).toBeInTheDocument();

    // But its absence must be stated, not read as "onboarding is done".
    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.getByText(/onboarding status/i)).toBeInTheDocument();

    // A failed read is not evidence of a migration in progress either.
    expect(screen.queryByText(/migration in progress/i)).not.toBeInTheDocument();
  });

  it('recovers when the status read succeeds', async () => {
    let attempt = 0;
    server.use(
      http.get('/api/dashboard', () => HttpResponse.json(DASH)),
      http.get('/api/onboarding/status', () => {
        attempt += 1;
        return attempt === 1
          ? new HttpResponse(null, { status: 503 })
          : HttpResponse.json({ ...OB, signedOff: false, entitiesImported: true });
      }),
    );
    renderDashboard();

    await userEvent.click(await screen.findByRole('button', { name: /retry/i }));

    expect(await screen.findByText(/migration in progress/i)).toBeInTheDocument();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('still redirects an empty org once the status is known', async () => {
    server.use(
      http.get('/api/dashboard', () => HttpResponse.json(DASH)),
      http.get('/api/onboarding/status', () =>
        HttpResponse.json({ ...OB, hasJournalData: false, signedOff: false }),
      ),
    );
    renderDashboard();

    expect(await screen.findByText('onboarding page')).toBeInTheDocument();
  });

  it('shows neither the notice nor the banner for an operational org', async () => {
    server.use(
      http.get('/api/dashboard', () => HttpResponse.json(DASH)),
      http.get('/api/onboarding/status', () => HttpResponse.json(OB)),
    );
    renderDashboard();

    expect(await screen.findByText('Trust total')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.queryByText(/migration in progress/i)).not.toBeInTheDocument();
  });
});
