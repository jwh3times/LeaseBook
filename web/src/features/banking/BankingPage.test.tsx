import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { trackInteraction } from '@/lib/telemetry';
import { server } from '@/test/mocks/server';
import { BankingPage } from './BankingPage';

vi.mock('@/lib/telemetry', () => ({ trackInteraction: vi.fn() }));

const BALANCES = {
  rows: [{ bankAccountId: 'acct1', name: 'Operating Trust', book: 1300, cleared: 1500, uncleared: -200 }],
};

const REGISTER = {
  rows: [
    {
      journalLineId: 'l1',
      date: '2026-02-01',
      description: 'Rent deposit',
      propertyId: null,
      deposit: 1500,
      withdrawal: null,
      status: 1, // cleared
    },
    {
      journalLineId: 'l2',
      date: '2026-02-03',
      description: 'Owner draw',
      propertyId: null,
      deposit: null,
      withdrawal: 200,
      status: 0, // uncleared
    },
  ],
  total: 2,
  totals: {
    book: 1300,
    cleared: 1500,
    uncleared: -200,
    unclearedCount: 1,
    depositsInView: 1500,
    withdrawalsInView: -200,
  },
};

// Base handlers exclude the register so each test supplies its own (MSW resolves first-match-first
// within a single server.use call, so a per-test register handler must not collide with a base one).
function baseHandlers() {
  return [
    http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
    http.get('/api/accounting/banks/balances', () => HttpResponse.json(BALANCES)),
    http.get('/api/directory/properties', () =>
      HttpResponse.json({ items: [], page: 1, pageSize: 200, total: 0 }),
    ),
    http.get('/api/accounting/reconciliations', () => HttpResponse.json({ rows: [] })),
  ];
}

const registerHandler = (body: Record<string, unknown>) =>
  http.get('/api/accounting/banks/:id/register', () => HttpResponse.json(body));

function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  render(
    <QueryClientProvider client={queryClient}>
      <BankingPage />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  document.body.innerHTML = '';
  vi.clearAllMocks();
});

describe('BankingPage register view', () => {
  it('renders the account tab, balance strip, and register rows with status badges', async () => {
    server.use(...baseHandlers(), registerHandler(REGISTER));
    renderPage();

    // Account tab with the book balance.
    expect(await screen.findByRole('button', { name: /Operating Trust/ })).toBeInTheDocument();

    // Wait for the register to load, then assert rows + status badges (label, not color alone).
    await screen.findByText('Rent deposit');
    const table = screen.getByRole('table');
    expect(within(table).getByText('Owner draw')).toBeInTheDocument();
    expect(within(table).getByText('Cleared')).toBeInTheDocument();
    expect(within(table).getByText('Uncleared')).toBeInTheDocument();

    // Balance strip (totals from the register read).
    expect(screen.getByText('Book balance')).toBeInTheDocument();
    expect(screen.getByText('Cleared balance')).toBeInTheDocument();
    expect(screen.getByText('1 item')).toBeInTheDocument();
    expect(screen.getAllByText('$1,300.00').length).toBeGreaterThan(0);
    expect(screen.getAllByText('$1,500.00').length).toBeGreaterThan(0);
  });

  it('shows the empty state when the register has no rows', async () => {
    server.use(
      ...baseHandlers(),
      registerHandler({
        rows: [],
        total: 0,
        totals: { book: 0, cleared: 0, uncleared: 0, unclearedCount: 0, depositsInView: 0, withdrawalsInView: 0 },
      }),
    );
    renderPage();
    expect(await screen.findByText('No transactions yet')).toBeInTheDocument();
  });
});

describe('BankingPage reconcile mode', () => {
  it('reconciles to $0.00 and finalizes — clearing the ticked items through the port', async () => {
    let clearancesBody: { journalLineIds?: string[] } | undefined;
    let started = false;
    let finalized = false;
    server.use(
      ...baseHandlers(),
      registerHandler(REGISTER),
      http.post('/api/accounting/banks/clearances', async ({ request }) => {
        clearancesBody = (await request.json()) as { journalLineIds?: string[] };
        return HttpResponse.json({ affected: 1 });
      }),
      http.post('/api/accounting/reconciliations', () => {
        started = true;
        return HttpResponse.json({
          id: 'rec1',
          bankAccountId: 'acct1',
          year: 2026,
          month: 2,
          statementEndingBalance: 1300,
          clearedBalance: 1300,
          difference: 0,
          status: 'in_progress',
          finalizedAt: null,
        });
      }),
      http.post('/api/accounting/reconciliations/:id/finalize', () => {
        finalized = true;
        return HttpResponse.json({
          id: 'rec1',
          bankAccountId: 'acct1',
          year: 2026,
          month: 2,
          statementEndingBalance: 1300,
          clearedBalance: 1300,
          difference: 0,
          status: 'finalized',
          finalizedAt: '2026-06-21T00:00:00Z',
        });
      }),
    );
    renderPage();

    // Start reconcile-in-place: one click (the ≤ 2-click budget).
    await userEvent.click(await screen.findByRole('button', { name: 'Reconcile account' }));
    expect(trackInteraction).toHaveBeenCalledWith('start-reconcile', 1, true);

    // Statement balance prefilled with the book balance; difference is non-zero until items are ticked.
    expect(screen.getByLabelText('Statement ending balance')).toHaveValue('1300.00');

    // Tick all uncleared → difference drives to $0.00 → Finalize appears.
    await userEvent.click(screen.getByRole('button', { name: 'Select all uncleared' }));
    const finalizeButton = await screen.findByRole('button', { name: 'Finalize' });

    await userEvent.click(finalizeButton);

    await vi.waitFor(() => expect(finalized).toBe(true));
    expect(clearancesBody?.journalLineIds).toEqual(['l2']);
    expect(started).toBe(true);

    // Back out of reconcile mode (balance strip returns).
    expect(await screen.findByText('Book balance')).toBeInTheDocument();
  });
});
