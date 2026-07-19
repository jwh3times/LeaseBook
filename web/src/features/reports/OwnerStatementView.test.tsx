import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { server } from '@/test/mocks/server';
import { OwnerStatementView } from './OwnerStatementView';
import type { StatementFilters } from './reports';

const FILTERS: StatementFilters = { basis: 'cash', year: 2026, month: 5 };

const STATEMENT = {
  ownerId: 'owner-1',
  ownerName: 'Helen Ford',
  propertyAddress: '101 Elm St',
  year: 2026,
  month: 5,
  basis: 'cash',
  beginning: 1500.0,
  ending: 1750.0,
  sections: [
    {
      key: 'income',
      title: 'Income — rent collected',
      subtotal: 2000.0,
      lines: [
        {
          entryId: 'e1',
          date: '2026-05-01',
          description: 'Rent May 2026',
          amount: 2000.0,
          eventType: 'RentCharged',
          eventSubtype: null,
          propertyAddress: '101 Elm St',
        },
      ],
    },
    {
      key: 'expenses',
      title: 'Operating expenses',
      subtotal: -250.0,
      lines: [
        {
          entryId: 'e2',
          date: '2026-05-10',
          description: 'Plumbing repair',
          amount: -250.0,
          eventType: 'OwnerExpense',
          eventSubtype: null,
          propertyAddress: null,
        },
      ],
    },
    {
      key: 'applied',
      title: 'Applied deposits & credits',
      subtotal: 0,
      lines: [],
    },
  ],
  fiduciary: {
    pmIncomeExcluded: true,
    depositsRecognizedOnApplication: true,
    balanced: true,
    variance: 0,
    latestReconciledBank: {
      bankAccountId: 'bank-1',
      year: 2026,
      month: 4,
      statementEndingBalance: 5000.0,
      finalizedAt: '2026-05-01T00:00:00Z',
    },
  },
  branding: {
    companyName: 'Acme PM',
    logoBlobRef: null,
    parenthesizedNegatives: false,
  },
};

function renderView(overrides?: Partial<typeof STATEMENT>) {
  const stmt = { ...STATEMENT, ...overrides };
  const onFiltersChange = vi.fn();
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <OwnerStatementView
          ownerId="owner-1"
          statement={stmt as Parameters<typeof OwnerStatementView>[0]['statement']}
          filters={FILTERS}
          onFiltersChange={onFiltersChange}
        />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return { onFiltersChange };
}

beforeEach(() => {
  document.body.innerHTML = '';
  vi.clearAllMocks();
  server.use(http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })));
});

describe('OwnerStatementView', () => {
  it('renders owner name, property, period and basis in the header', () => {
    renderView();
    expect(screen.getByRole('heading', { name: 'Owner statement' })).toBeInTheDocument();
    // Multiple Helen Ford elements (page header + statement doc header) — check that at least one exists.
    expect(screen.getAllByText(/Helen Ford/).length).toBeGreaterThanOrEqual(1);
    // The page header p has all the metadata in one element.
    expect(
      screen.getByText(
        (_, el) =>
          el?.tagName === 'P' &&
          (el.textContent ?? '').includes('Helen Ford') &&
          (el.textContent ?? '').includes('101 Elm St') &&
          (el.textContent ?? '').includes('May 2026') &&
          (el.textContent ?? '').includes('cash basis'),
      ),
    ).toBeInTheDocument();
  });

  it('renders beginning and ending balances', () => {
    renderView();
    expect(screen.getByText('Beginning balance')).toBeInTheDocument();
    expect(screen.getByText('Ending balance')).toBeInTheDocument();
    expect(screen.getAllByText('$1,500.00').length).toBeGreaterThan(0);
    expect(screen.getAllByText('$1,750.00').length).toBeGreaterThan(0);
  });

  it('renders statement sections with their lines', () => {
    renderView();
    expect(screen.getByText('Income — rent collected')).toBeInTheDocument();
    expect(screen.getByText('Rent May 2026')).toBeInTheDocument();
    expect(screen.getByText('Operating expenses')).toBeInTheDocument();
    expect(screen.getByText('Plumbing repair')).toBeInTheDocument();
  });

  it('renders fiduciary checks using icon + label (status never by color alone)', () => {
    renderView();
    const sidebar = screen.getByRole('list');
    const items = within(sidebar).getAllByRole('listitem');
    // All three checks should be present.
    expect(items.length).toBeGreaterThanOrEqual(3);
    // Checks include textual label (never color-only).
    expect(within(sidebar).getByText(/PM income excluded/)).toBeInTheDocument();
    expect(within(sidebar).getByText(/Deposits recognized on application/)).toBeInTheDocument();
    // Check icons have accessible labels.
    expect(sidebar.querySelectorAll('[aria-label="Pass"]').length).toBeGreaterThan(0);
  });

  it('renders the $0.00 variance reconciles-to check', () => {
    renderView();
    expect(screen.getByRole('status')).toHaveTextContent(/0.00 variance/);
  });

  it('shows a warning variant when the statement is not balanced', () => {
    // The schema types latestReconciledBank as a required object but the runtime value can be null
    // when no reconciliation has been done yet — cast for test-data purposes only.
    const stmtPatch = {
      fiduciary: {
        pmIncomeExcluded: true,
        depositsRecognizedOnApplication: true,
        balanced: false,
        variance: 42.5,
        latestReconciledBank: null,
      },
    } as unknown as Partial<typeof STATEMENT>;
    renderView(stmtPatch);
    expect(screen.getByRole('alert')).toHaveTextContent(/42.50/);
  });

  it('renders PDF and CSV export buttons', () => {
    renderView();
    expect(screen.getByRole('button', { name: /PDF/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Export CSV/i })).toBeInTheDocument();
  });

  it('period picker opens and allows changing basis', async () => {
    const { onFiltersChange } = renderView();
    const periodBtn = screen.getByRole('button', { name: /May 2026/ });
    await userEvent.click(periodBtn);

    const dialog = screen.getByRole('dialog', { name: 'Select period' });
    expect(dialog).toBeInTheDocument();

    const accrualBtn = within(dialog).getByRole('button', { name: 'Accrual' });
    await userEvent.click(accrualBtn);

    expect(onFiltersChange).toHaveBeenCalledWith(expect.objectContaining({ basis: 'accrual' }));
  });

  it('deliver button calls the deliver endpoint and shows queued status', async () => {
    let delivered = false;
    server.use(
      http.post('/api/statements/:ownerId/deliver', () => {
        delivered = true;
        return new HttpResponse(null, { status: 200 });
      }),
    );

    renderView();
    const deliverBtn = screen.getByRole('button', { name: /Deliver to owner/i });
    await userEvent.click(deliverBtn);

    await vi.waitFor(() => expect(delivered).toBe(true));
    expect(await screen.findByText(/Queued for delivery/)).toBeInTheDocument();
  });

  it('shows error status when deliver returns 409', async () => {
    server.use(
      http.post('/api/statements/:ownerId/deliver', () =>
        HttpResponse.json(
          { code: 'statement_not_balanced', detail: 'Statement is not balanced.' },
          { status: 409 },
        ),
      ),
    );

    renderView();
    await userEvent.click(screen.getByRole('button', { name: /Deliver to owner/i }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/Statement is not balanced/);
  });
});
