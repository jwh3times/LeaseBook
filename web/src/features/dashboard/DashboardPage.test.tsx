import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { beforeEach, describe, expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { DashboardPage } from './DashboardPage';

const DASH = {
  kpis: {
    trustTotal: 483620.69,
    ownersPayable: 111967.4,
    uncleared: 250.0,
    unclearedCount: 3,
    collectedMtd: 1380,
    collectedTarget: 28000,
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
      { bankAccountId: 'b3', name: 'PM Operating', book: 38240.55, unclearedCount: 0 },
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

function renderDashboard() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      { path: '/dashboard', element: <DashboardPage /> },
      { path: '/owners/:id', element: <div>owner page</div> },
      { path: '/banking', element: <div>banking page</div> },
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
    server.use(http.get('/api/dashboard', () => HttpResponse.json(DASH)));
    renderDashboard();

    expect(await screen.findByText('Trust total')).toBeInTheDocument();
    expect(screen.getByText('Owners payable')).toBeInTheDocument();
    expect(screen.getByText(/483,620\.69/)).toBeInTheDocument();

    // The hero is named (0-click owner balances) and shows the relabeled roll-up.
    expect(screen.getByText('Hargrove Family Trust')).toBeInTheDocument();
    expect(screen.getByText('All other owners')).toBeInTheDocument();

    // Trust accounts and needs-attention items render.
    expect(screen.getByText('Operating Trust')).toBeInTheDocument();
    expect(screen.getByText('Deposits awaiting application')).toBeInTheDocument();

    // Per-account uncleared count renders on the bank summary card.
    expect(screen.getByText('3 uncleared')).toBeInTheDocument();
    expect(screen.getAllByText('Reconciled')).toHaveLength(2);

    // The "Uncleared" StatCard shows the non-zero count badge.
    expect(screen.getByText('3 items')).toBeInTheDocument();
  });

  it('navigates to an owner from the hero', async () => {
    server.use(http.get('/api/dashboard', () => HttpResponse.json(DASH)));
    renderDashboard();
    await userEvent.click(await screen.findByText('Hargrove Family Trust'));
    expect(await screen.findByText('owner page')).toBeInTheDocument();
  });

  it('deep-links an action item to its route', async () => {
    server.use(http.get('/api/dashboard', () => HttpResponse.json(DASH)));
    renderDashboard();
    await userEvent.click(await screen.findByText('Deposits awaiting application'));
    expect(await screen.findByText('banking page')).toBeInTheDocument();
  });

  it('shows an error state when the dashboard fails', async () => {
    server.use(http.get('/api/dashboard', () => new HttpResponse(null, { status: 500 })));
    renderDashboard();
    expect(await screen.findByText(/couldn't load the dashboard/i)).toBeInTheDocument();
  });

  beforeEach(() => {
    document.body.innerHTML = '';
  });
});
