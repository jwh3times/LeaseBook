import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { StrictMode } from 'react';
import { createMemoryRouter, type InitialEntry, RouterProvider, useNavigate } from 'react-router';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { RecordNavProvider } from '@/components/recordNav';
import { spentInteractions, trackInteraction } from '@/lib/telemetry';
import { server } from '@/test/mocks/server';
import type { TenantLedgerEntry } from './ledger';
import { LedgerPage } from './LedgerPage';

// Only the budget sample is stubbed; `spentInteractions`/`readSpentInteractions` stay real so the
// #408 tests below exercise the actual navigation-state round trip rather than a stand-in for it.
vi.mock('@/lib/telemetry', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/telemetry')>()),
  trackInteraction: vi.fn(),
}));

const DETAIL = {
  id: 't1',
  displayName: 'Jasmine Carter',
  contact: { email: null, phone: '828-555-0100' },
  lifecycleStatus: 'current',
  financialStanding: { delinquentBalance: 1450, unappliedCredit: 75 },
  lease: {
    startDate: '2025-06-01',
    endDate: '2026-05-31',
    rent: 1450,
    depositRequired: 1450,
    status: 'active',
  },
  unitLabel: '#2B',
  propertyAddress: '412 Oakmont Ave',
  ownerId: 'o1',
  ownerName: 'Hargrove',
  balance: 1450,
  depositHeld: 1450,
};

// Ledger rows in server (ascending) order; the page reverses for newest-first display.
const ROWS: TenantLedgerEntry[] = [
  {
    entryId: 'e1',
    date: '2026-02-01',
    eventType: 'RentCharged',
    eventSubtype: null,
    category: 'Rent',
    description: 'Feb rent',
    charge: 1450,
    payment: 0,
    balance: 1450,
    isVoided: false,
    reversesEntryId: null,
  },
  {
    entryId: 'e2',
    date: '2026-02-10',
    eventType: 'FeeCharged',
    eventSubtype: 'late',
    category: 'Late Fee',
    description: 'Late fee',
    charge: 50,
    payment: 0,
    balance: 1500,
    isVoided: false,
    reversesEntryId: null,
  },
  {
    entryId: 'e3',
    date: '2026-02-15',
    eventType: 'PaymentReceived',
    eventSubtype: 'ACH',
    category: 'Payment',
    description: 'Rent payment',
    charge: 0,
    payment: 1500,
    balance: 0,
    isVoided: false,
    reversesEntryId: null,
  },
];

function detailHandler(detail = DETAIL) {
  return http.get('/api/directory/tenants/t1', () => HttpResponse.json(detail));
}

function ledgerHandler(rows: TenantLedgerEntry[] = ROWS) {
  return http.get('/api/accounting/tenants/:tenantId/ledger', () =>
    HttpResponse.json({ tenantId: 't1', balance: rows.at(-1)?.balance ?? 0, rows }),
  );
}

// Stands in for the palette: a PUSH to the composed URL carrying the interactions it already spent.
// The #408 tests navigate through this rather than starting at the composed URL, because the two
// arrivals are not the same thing — a PUSH is the palette paying, an initial render is a reload.
function ComposeLauncher() {
  const navigate = useNavigate();
  return (
    <button onClick={() => void navigate('/tenants/t1?compose=payment', { state: LAUNCH_STATE })}>
      launch payment
    </button>
  );
}

const LAUNCH_STATE = spentInteractions(2);

function renderLedger(initialEntry: InitialEntry = '/tenants/t1') {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  // The launcher sits alongside the page in the SAME route element, so navigating to the composed URL
  // reconciles rather than remounts `LedgerPage` — exactly what React Router does in the app when the
  // operator runs the palette action from a ledger they are already on.
  const router = createMemoryRouter(
    [
      {
        path: '/tenants/:id',
        element: (
          <>
            <ComposeLauncher />
            <LedgerPage />
          </>
        ),
      },
    ],
    { initialEntries: [initialEntry] },
  );
  // StrictMode mirrors how the app actually mounts, so effect-ordering bugs that only appear under
  // double-invocation (see LedgerComposer.test.tsx) surface here rather than in the browser.
  return render(
    <StrictMode>
      <QueryClientProvider client={queryClient}>
        <RecordNavProvider>
          <RouterProvider router={router} />
        </RecordNavProvider>
      </QueryClientProvider>
    </StrictMode>,
  );
}

beforeAll(() => {
  // @tanstack/react-virtual measures the scroll element via offsetWidth/offsetHeight and ResizeObserver
  // borderBoxSize — jsdom reports 0 and ships no observer. Give the element a real size and an observer
  // that fires once so the virtualizer computes a visible window and renders the (small) test datasets.
  globalThis.ResizeObserver = class {
    private readonly cb: ResizeObserverCallback;
    constructor(cb: ResizeObserverCallback) {
      this.cb = cb;
    }
    observe() {
      this.cb(
        [
          { borderBoxSize: [{ inlineSize: 800, blockSize: 600 }] },
        ] as unknown as ResizeObserverEntry[],
        this,
      );
    }
    unobserve() {}
    disconnect() {}
  };
  Object.defineProperty(HTMLElement.prototype, 'offsetWidth', {
    configurable: true,
    get: () => 800,
  });
  Object.defineProperty(HTMLElement.prototype, 'offsetHeight', {
    configurable: true,
    get: () => 600,
  });
});

beforeEach(() => {
  document.body.innerHTML = '';
  vi.clearAllMocks();
});

function composerHandlers() {
  return [
    http.get('/api/settings/banks', () =>
      HttpResponse.json([
        {
          id: 'trust1',
          name: 'Operating Trust',
          institution: null,
          mask: null,
          purpose: 'trust',
          isActive: true,
        },
      ]),
    ),
    http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
    http.post('/api/accounting/tenants/:tenantId/payments', () =>
      HttpResponse.json({ entryId: 'pay1' }),
    ),
  ];
}

describe('LedgerPage', () => {
  // #408: the palette's "Record payment → X" spends ⌘K + the pick before this page exists and
  // hands the count over in history state. The page is the seam that has to read it back out — and
  // it must open the composer even though it is not remounting.
  it('opens the composer and reports the true count on a palette-launched payment', async () => {
    server.use(detailHandler(), ledgerHandler(), ...composerHandlers());
    renderLedger();

    await userEvent.click(await screen.findByRole('button', { name: 'launch payment' }));

    await screen.findByText('Operating Trust'); // banks loaded → the auto-opened composer is ready
    await userEvent.type(screen.getByLabelText('Amount'), '1450');
    await userEvent.keyboard('{Enter}');

    await vi.waitFor(() =>
      expect(trackInteraction).toHaveBeenCalledWith('record-payment', 3, true),
    );
  });

  // A reload or a Back lands on the same history entry, state and all. Those cost the operator one
  // interaction, not the palette's two, so the seed must not apply a second time.
  it('does not re-charge the palette’s interactions on a replayed history entry', async () => {
    server.use(detailHandler(), ledgerHandler(), ...composerHandlers());
    renderLedger({
      pathname: '/tenants/t1',
      search: '?compose=payment',
      state: spentInteractions(2),
    });

    await screen.findByText('Operating Trust');
    await userEvent.type(screen.getByLabelText('Amount'), '1450');
    await userEvent.keyboard('{Enter}');

    await vi.waitFor(() =>
      expect(trackInteraction).toHaveBeenCalledWith('record-payment', 2, true),
    );
  });

  it('ignores a spent-interaction count on a history entry that asked for no composer', async () => {
    server.use(detailHandler(), ledgerHandler(), ...composerHandlers());
    renderLedger({ pathname: '/tenants/t1', state: spentInteractions(2) });

    await userEvent.click(await screen.findByRole('button', { name: 'Record payment' }));
    await screen.findByText('Operating Trust');
    await userEvent.type(screen.getByLabelText('Amount'), '1450');
    await userEvent.keyboard('{Enter}');

    await vi.waitFor(() =>
      expect(trackInteraction).toHaveBeenCalledWith('record-payment', 2, true),
    );
  });

  it('renders the header and the ledger rows with running balances', async () => {
    server.use(detailHandler(), ledgerHandler());
    renderLedger();

    expect(await screen.findByRole('heading', { name: 'Jasmine Carter' })).toBeInTheDocument();
    expect(screen.getByText('Current balance')).toBeInTheDocument();
    expect(screen.getByText('Deposit held')).toBeInTheDocument();
    expect(screen.getByText('Liability · not income')).toBeInTheDocument();
    expect(screen.getByText('Delinquent')).toBeInTheDocument();
    expect(screen.getByText('Credit on file')).toBeInTheDocument();

    expect(await screen.findByText('Feb rent')).toBeInTheDocument();
    expect(screen.getByText('Rent payment')).toBeInTheDocument();
    expect(screen.getByText('3 entries · running balance')).toBeInTheDocument();
    // Category badge inside the grid (the type filter also lists "Late Fee" as an option).
    expect(within(screen.getByRole('grid')).getByText('Late Fee')).toBeInTheDocument();
  });

  it('renders voided rows struck and reversal rows linked', async () => {
    const rows = [
      {
        entryId: 'v1',
        date: '2026-02-01',
        eventType: 'RentCharged',
        eventSubtype: null,
        category: 'Rent',
        description: 'Feb rent',
        charge: 1450,
        payment: 0,
        balance: 1450,
        isVoided: true,
        reversesEntryId: null,
      },
      {
        entryId: 'v2',
        date: '2026-02-02',
        eventType: 'EntryVoided',
        eventSubtype: null,
        category: 'EntryVoided',
        description: 'VOID: typo',
        charge: 0,
        payment: 1450,
        balance: 0,
        isVoided: false,
        reversesEntryId: 'v1',
      },
    ];
    server.use(detailHandler(), ledgerHandler(rows));
    const { container } = renderLedger();

    expect(await screen.findByText('Voided')).toBeInTheDocument();
    expect(screen.getByText('Reversal')).toBeInTheDocument();
    expect(container.querySelector('[data-entry-id="v1"]')).toHaveClass('voided');
  });

  it('narrows by type and by date filter', async () => {
    server.use(detailHandler(), ledgerHandler());
    renderLedger();
    await screen.findByText('Feb rent');

    await userEvent.selectOptions(screen.getByLabelText('Filter by type'), 'Payment');
    expect(screen.queryByText('Feb rent')).not.toBeInTheDocument();
    expect(screen.getByText('Rent payment')).toBeInTheDocument();

    // Clear, then a from-date that excludes the earlier rows.
    await userEvent.click(screen.getByRole('button', { name: /clear/i }));
    fireEvent.change(screen.getByLabelText('From date'), { target: { value: '2026-02-12' } });
    expect(screen.queryByText('Feb rent')).not.toBeInTheDocument();
    expect(screen.getByText('Rent payment')).toBeInTheDocument();
  });

  it('exports the ledger via the CSV endpoint', async () => {
    let csvHit = false;
    server.use(
      detailHandler(),
      ledgerHandler(),
      http.get('/api/accounting/tenants/t1/ledger.csv', () => {
        csvHit = true;
        return new HttpResponse('Date,Category,Description,Charge,Payment,Balance,Status\n', {
          headers: { 'Content-Type': 'text/csv' },
        });
      }),
    );
    globalThis.URL.createObjectURL = vi.fn(() => 'blob:test');
    globalThis.URL.revokeObjectURL = vi.fn();

    renderLedger();
    await screen.findByText('Feb rent');
    await userEvent.click(screen.getByRole('button', { name: /export/i }));

    await vi.waitFor(() => expect(csvHit).toBe(true));
  });

  it('shows an empty state with no activity', async () => {
    server.use(detailHandler(), ledgerHandler([]));
    renderLedger();
    expect(await screen.findByText('No ledger activity yet')).toBeInTheDocument();
  });

  it('shows an error state when the ledger fails', async () => {
    server.use(
      detailHandler(),
      http.get(
        '/api/accounting/tenants/:tenantId/ledger',
        () => new HttpResponse(null, { status: 500 }),
      ),
    );
    renderLedger();
    expect(await screen.findByText("Couldn't load the ledger")).toBeInTheDocument();
  });

  it('carries the support reference and retries the ledger in place', async () => {
    const reference = 'aa11bb22aa11bb22aa11bb22aa11bb22';
    let attempt = 0;
    server.use(
      detailHandler(),
      http.get('/api/accounting/tenants/:tenantId/ledger', () => {
        attempt += 1;
        return attempt === 1
          ? HttpResponse.json(
              { detail: 'The ledger projection is rebuilding.', correlationId: reference },
              { status: 503 },
            )
          : HttpResponse.json({ tenantId: 't1', balance: 1500, rows: ROWS });
      }),
    );
    renderLedger();

    expect(await screen.findByText("Couldn't load the ledger")).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent('The ledger projection is rebuilding.');
    expect(screen.getByText(`Reference: ${reference}`)).toBeInTheDocument();

    // A tenant ledger that failed to load is not a tenant with no activity — the difference decides
    // whether the operator re-enters a payment.
    expect(screen.queryByText('No ledger activity yet')).toBeNull();

    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));

    expect(await screen.findByText('Feb rent')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('separates a failed tenant read from a tenant that is genuinely gone', async () => {
    const reference = 'cc33dd44cc33dd44cc33dd44cc33dd44';
    server.use(
      http.get('/api/directory/tenants/t1', () =>
        HttpResponse.json(
          { detail: 'The directory is unavailable.', correlationId: reference },
          { status: 503 },
        ),
      ),
      ledgerHandler(),
    );
    renderLedger();

    expect(await screen.findByText("Couldn't load this tenant")).toBeInTheDocument();
    expect(screen.getByText(`Reference: ${reference}`)).toBeInTheDocument();
    expect(screen.queryByText('Tenant not found')).toBeNull();
  });

  it('still says the tenant is not found on a 404, which retrying cannot fix', async () => {
    server.use(
      http.get('/api/directory/tenants/t1', () => new HttpResponse(null, { status: 404 })),
      ledgerHandler(),
    );
    renderLedger();

    expect(await screen.findByText('Tenant not found')).toBeInTheDocument();
    expect(screen.queryByText("Couldn't load this tenant")).toBeNull();
  });

  it('is keyboard navigable — arrow keys move the selected row', async () => {
    server.use(detailHandler(), ledgerHandler());
    const { container } = renderLedger();
    await screen.findByText('Feb rent');

    const grid = screen.getByRole('grid');
    grid.focus();
    await userEvent.keyboard('{ArrowDown}');

    // Display is newest-first [e3, e2, e1]; ArrowDown moves selection to index 1 (e2).
    expect(container.querySelector('[data-entry-id="e2"]')).toHaveAttribute(
      'aria-selected',
      'true',
    );
  });
});
