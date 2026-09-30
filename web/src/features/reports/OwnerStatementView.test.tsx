import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router';
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
  server.use(
    http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
    // The staff note overlay (#468). Unstubbed, MSW bypasses it and the failed read renders an alert.
    http.get('/api/statements/:ownerId/internal-notes', () => HttpResponse.json({ notes: [] })),
  );
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

  it('opens from the issued prior statement and itemizes what changed since (ADR-045)', () => {
    renderView({
      beginning: 1595.0,
      carryForward: {
        issuedYear: 2026,
        issuedMonth: 4,
        issuedAt: '2026-05-02T14:00:00Z',
        issuedEnding: 1500.0,
        lines: [
          {
            entryId: 'cf-1',
            date: '2026-04-28',
            postedAt: '2026-05-09T16:30:00Z',
            eventType: 'FeeCharged',
            description: 'Gutter cleaning recharge',
            propertyAddress: '101 Elm St',
            amount: 95.0,
          },
        ],
        unitemized: 0,
        total: 95.0,
      },
    } as unknown as Partial<typeof STATEMENT>);

    expect(
      screen.getByText(/Beginning balance, as issued for .*2026 \(issued May 2, 2026\)/),
    ).toBeInTheDocument();
    const adjustments = screen.getByRole('region', { name: 'Prior-period adjustments' });
    expect(
      within(adjustments).getByText('Apr 28, 2026 · Gutter cleaning recharge'),
    ).toBeInTheDocument();
    expect(within(adjustments).getByText(/Posted May 9, 2026/)).toBeInTheDocument();
    expect(within(adjustments).queryByText('Unitemized adjustments')).not.toBeInTheDocument();
    expect(screen.getByText('Adjusted beginning balance')).toBeInTheDocument();
    expect(screen.queryByText('Beginning balance')).not.toBeInTheDocument();
  });

  it('labels an unitemized remainder instead of hiding it', () => {
    renderView({
      carryForward: {
        issuedYear: 2026,
        issuedMonth: 4,
        issuedAt: '2026-05-02T14:00:00Z',
        issuedEnding: 1510.0,
        lines: [],
        unitemized: -10.0,
        total: -10.0,
      },
    } as unknown as Partial<typeof STATEMENT>);

    const adjustments = screen.getByRole('region', { name: 'Prior-period adjustments' });
    expect(within(adjustments).getByText('Unitemized adjustments')).toBeInTheDocument();
  });

  it('keeps the plain beginning row when nothing carries forward', () => {
    renderView();
    expect(
      screen.queryByRole('region', { name: 'Prior-period adjustments' }),
    ).not.toBeInTheDocument();
    expect(screen.queryByText('Adjusted beginning balance')).not.toBeInTheDocument();
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
    let deliveredUrl: string | undefined;
    server.use(
      http.post('/api/statements/:ownerId/deliver', ({ request }) => {
        deliveredUrl = request.url;
        return new HttpResponse(null, { status: 200 });
      }),
    );

    renderView();
    const deliverBtn = screen.getByRole('button', { name: /Deliver to owner/i });
    await userEvent.click(deliverBtn);

    await vi.waitFor(() => expect(deliveredUrl).toBeDefined());
    // The host sends to the owner's address on file; the request never names a recipient.
    expect(new URL(deliveredUrl!).searchParams.has('toEmail')).toBe(false);
    expect(await screen.findByText(/Queued for delivery/)).toBeInTheDocument();
  });

  it('explains when the owner has no email address on file', async () => {
    server.use(
      http.post('/api/statements/:ownerId/deliver', () =>
        HttpResponse.json(
          {
            code: 'owner_email_missing',
            detail:
              "This owner has no email address on file. Add one to the owner's record, then deliver the statement.",
          },
          { status: 409 },
        ),
      ),
    );

    renderView();
    await userEvent.click(screen.getByRole('button', { name: /Deliver to owner/i }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/no email address on file/);
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

// #468 (ADR-047): the staff view annotates lines with their internal notes from a separate read, so
// `StatementView` — what the PDF, the CSV and the issued copy are built from — never carries one.
describe('OwnerStatementView internal notes', () => {
  const REFERENCE = 'a1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4';
  const lineOf = (text: string) => screen.getByText(text).closest('.pf-stmt-line') as HTMLElement;

  it('asks for the notes of the statement on screen and marks them as not on the owner’s copy', async () => {
    let requested: URL | undefined;
    server.use(
      http.get('/api/statements/:ownerId/internal-notes', ({ request }) => {
        requested = new URL(request.url);
        return HttpResponse.json({
          notes: [{ entryId: 'e2', internalNote: 'Vendor invoice disputed' }],
        });
      }),
    );
    renderView();

    expect(await screen.findByText('Vendor invoice disputed')).toBeInTheDocument();
    expect(requested?.pathname).toBe('/api/statements/owner-1/internal-notes');
    expect(Object.fromEntries(requested!.searchParams)).toEqual({
      basis: 'cash',
      year: '2026',
      month: '5',
    });

    const noted = lineOf('Plumbing repair');
    expect(noted).toHaveTextContent('Internal — not on the owner’s copy: Vendor invoice disputed');
    expect(within(lineOf('Rent May 2026')).queryByText(/Internal —/)).toBeNull();
  });

  it('annotates a prior-period adjustment by its entry id', async () => {
    server.use(
      http.get('/api/statements/:ownerId/internal-notes', () =>
        HttpResponse.json({ notes: [{ entryId: 'cf-1', internalNote: 'Posted late by staff' }] }),
      ),
    );
    renderView({
      carryForward: {
        issuedYear: 2026,
        issuedMonth: 4,
        issuedAt: '2026-05-02T14:00:00Z',
        issuedEnding: 1500.0,
        lines: [
          {
            entryId: 'cf-1',
            date: '2026-04-28',
            postedAt: '2026-05-09T16:30:00Z',
            eventType: 'FeeCharged',
            description: 'Gutter cleaning recharge',
            propertyAddress: null,
            amount: 95.0,
          },
        ],
        unitemized: 0,
        total: 95.0,
      },
    } as unknown as Partial<typeof STATEMENT>);

    const adjustments = screen.getByRole('region', { name: 'Prior-period adjustments' });
    expect(await within(adjustments).findByText('Posted late by staff')).toBeInTheDocument();
  });

  it('keeps the statement and reports the failure when the notes cannot load', async () => {
    let attempt = 0;
    server.use(
      http.get('/api/statements/:ownerId/internal-notes', () => {
        attempt += 1;
        return attempt === 1
          ? HttpResponse.json(
              { detail: 'The notes store is unavailable.', correlationId: REFERENCE },
              { status: 503 },
            )
          : HttpResponse.json({ notes: [{ entryId: 'e1', internalNote: 'Paid early' }] });
      }),
    );
    renderView();

    expect(await screen.findByText('The notes store is unavailable.')).toBeInTheDocument();
    expect(screen.getByText(`Reference: ${REFERENCE}`)).toBeInTheDocument();
    expect(screen.getByText(/internal notes couldn’t be loaded, so none are shown/i)).toBeVisible();
    // The failure annotates; it never gates. Every line and the ending balance still render.
    expect(screen.getByText('Rent May 2026')).toBeInTheDocument();
    expect(screen.getByText('Plumbing repair')).toBeInTheDocument();
    expect(screen.getByText('Ending balance')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText('Paid early')).toBeInTheDocument();
    expect(screen.queryByText(`Reference: ${REFERENCE}`)).toBeNull();
  });
});
