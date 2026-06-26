import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { RecordNavProvider } from '@/components/recordNav';
import { server } from '@/test/mocks/server';
import type { TenantLedgerEntry } from './ledger';
import { LedgerPage } from './LedgerPage';

const DETAIL = {
  id: 't1',
  displayName: 'Jasmine Carter',
  contact: { email: null, phone: '828-555-0100' },
  status: 'current',
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

function renderLedger() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter([{ path: '/tenants/:id', element: <LedgerPage /> }], {
    initialEntries: ['/tenants/t1'],
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <RecordNavProvider>
        <RouterProvider router={router} />
      </RecordNavProvider>
    </QueryClientProvider>,
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
});

describe('LedgerPage', () => {
  it('renders the header and the ledger rows with running balances', async () => {
    server.use(detailHandler(), ledgerHandler());
    renderLedger();

    expect(await screen.findByRole('heading', { name: 'Jasmine Carter' })).toBeInTheDocument();
    expect(screen.getByText('Current balance')).toBeInTheDocument();
    expect(screen.getByText('Deposit held')).toBeInTheDocument();
    expect(screen.getByText('Liability · not income')).toBeInTheDocument();

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
