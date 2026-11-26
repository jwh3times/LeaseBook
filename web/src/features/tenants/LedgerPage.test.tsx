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
    internalNote: null,
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
    internalNote: null,
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
    internalNote: null,
  },
];

function detailHandler(detail = DETAIL) {
  return http.get('/api/directory/tenants/t1', () => HttpResponse.json(detail));
}

function ledgerHandler(rows: TenantLedgerEntry[] = ROWS) {
  return http.get('/api/accounting/tenants/:tenantId/ledger', () =>
    HttpResponse.json({
      tenantId: 't1',
      balance: rows.at(-1)?.balance ?? 0,
      rows,
      owed: Math.max(Number(rows.at(-1)?.balance ?? 0), 0),
    }),
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
  // @tanstack/react-virtual measures the scroll element via clientWidth/clientHeight (offsetWidth/
  // offsetHeight and ResizeObserver borderBoxSize in releases before 3.14.21) — jsdom reports 0 and
  // ships no observer. Give the element a real size and an observer that fires once so the virtualizer
  // computes a visible window and renders the (small) test datasets.
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
  Object.defineProperty(Element.prototype, 'clientWidth', {
    configurable: true,
    get: () => 800,
  });
  Object.defineProperty(Element.prototype, 'clientHeight', {
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

  // #652: the composer bounds a credit by the ledger read's `owed`, and offers the allowance only to
  // an administrator. ROWS end at a zero balance, so any credit is larger than what is owed.
  it.each([
    ['PMAdmin', true],
    ['PMStaff', false],
  ])(
    'passes what is owed and whether a %s may allow more to the composer',
    async (role, allows) => {
      server.use(
        detailHandler(),
        ledgerHandler(),
        ...composerHandlers(),
        http.get('/api/auth/me', () =>
          HttpResponse.json({
            userId: 'u1',
            name: 'A',
            email: null,
            role,
            orgId: 'o1',
            orgName: null,
          }),
        ),
      );
      renderLedger();

      await userEvent.click(await screen.findByRole('button', { name: 'Add charge' }));
      await userEvent.selectOptions(await screen.findByLabelText('Charge type'), 'Credit');
      await userEvent.type(screen.getByLabelText('Amount'), '85');

      await vi.waitFor(() =>
        expect(screen.getByLabelText('Amount').closest('label')).toHaveTextContent(
          'Tenant owes $0.00',
        ),
      );
      await vi.waitFor(() =>
        expect(screen.queryByRole('checkbox', { name: /Allow a credit/ }) !== null).toBe(allows),
      );
    },
  );

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
        internalNote: null,
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
        internalNote: null,
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
          : HttpResponse.json({ tenantId: 't1', balance: 1500, rows: ROWS, owed: 1500 });
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

// #468 (ADR-047): staff see an entry's internal note beside its owner-facing description, marked as
// staff-only in words and a glyph rather than by colour.
describe('LedgerPage internal notes', () => {
  const rowOf = (text: string) => screen.getByText(text).closest('[role="row"]') as HTMLElement;

  it('shows a row’s internal note under its description, labelled as internal', async () => {
    server.use(
      detailHandler(),
      ledgerHandler([ROWS[0]!, { ...ROWS[2]!, internalNote: 'Paid at the office, receipt 88' }]),
    );
    renderLedger();

    await screen.findByText('Rent payment');
    const noted = rowOf('Rent payment');
    expect(within(noted).getByText('Internal note:')).toBeInTheDocument();
    expect(within(noted).getByText('Paid at the office, receipt 88')).toBeInTheDocument();
    expect(within(rowOf('Feb rent')).queryByText('Internal note:')).toBeNull();
  });

  it('tells the void dialog the voided row’s description', async () => {
    server.use(detailHandler(), ledgerHandler());
    renderLedger();

    await screen.findByText('Feb rent');
    await userEvent.click(within(rowOf('Feb rent')).getByRole('button', { name: 'Void entry' }));

    const dialog = await screen.findByRole('dialog', { name: 'Void entry' });
    expect(within(dialog).getByText('“Void — Feb rent”')).toBeInTheDocument();
  });
});

// #473: the refund-check action beside "Apply…" under the deposit held.
describe('LedgerPage refund checks', () => {
  const DEPOSIT_FUND = {
    source: 'deposit',
    bankAccountId: 'dep1',
    propertyId: null,
    ownerId: 'o1',
    held: 1450,
    nextCheckNumber: 1043,
  };

  it('disables Refund… with a reason only once the read confirms nothing is held', async () => {
    server.use(
      detailHandler(),
      ledgerHandler(),
      http.get('/api/refund-checks/options', () => HttpResponse.json({ funds: [] })),
    );
    renderLedger();

    const refund = await screen.findByRole('button', { name: 'Refund…' });
    await vi.waitFor(() => expect(refund).toBeDisabled());
    expect(refund).toHaveAccessibleDescription('No held deposit or prepaid credit to refund');
  });

  it('keeps Refund… available when the funds read fails, so the dialog can report it', async () => {
    server.use(
      detailHandler(),
      ledgerHandler(),
      http.get('/api/settings/banks', () => HttpResponse.json([])),
      http.get('/api/refund-checks/options', () =>
        HttpResponse.json(
          { detail: 'Funds unavailable.', correlationId: 'abcdabcdabcdabcdabcdabcdabcdabcd' },
          { status: 500 },
        ),
      ),
    );
    renderLedger();

    const refund = await screen.findByRole('button', { name: 'Refund…' });
    expect(refund).toBeEnabled();
    await userEvent.click(refund);
    const dialog = await screen.findByRole('dialog', { name: 'Refund check' });
    expect(await within(dialog).findByText('Funds unavailable.')).toBeInTheDocument();
    expect(within(dialog).queryByText(/No held deposit/)).toBeNull();
  });

  it('opens the refund dialog prefilled from the tenant header', async () => {
    server.use(
      detailHandler(),
      ledgerHandler(),
      http.get('/api/settings/banks', () =>
        HttpResponse.json([
          {
            id: 'dep1',
            name: 'Security Deposit Trust',
            institution: null,
            mask: null,
            purpose: 'deposit',
            isActive: true,
          },
        ]),
      ),
      http.get('/api/refund-checks/options', () => HttpResponse.json({ funds: [DEPOSIT_FUND] })),
    );
    renderLedger();

    await userEvent.click(await screen.findByRole('button', { name: 'Refund…' }));
    const dialog = await screen.findByRole('dialog', { name: 'Refund check' });
    expect(await within(dialog).findByLabelText('Amount')).toHaveValue('1450.00');
    expect(within(dialog).getByLabelText('Pay to the order of')).toHaveValue('Jasmine Carter');
    expect(within(dialog).getByLabelText('Mailing address')).toHaveValue('412 Oakmont Ave');
  });
});

// #781: a lease is given to a tenant from their ledger, with no import. Creating one posts nothing.
describe('LedgerPage add a lease', () => {
  const NO_LEASE = { ...DETAIL, lease: null, unitLabel: null, propertyAddress: null, balance: 0 };
  const LEASED = {
    ...NO_LEASE,
    lease: {
      id: 'l9',
      unitId: 'u1',
      startDate: '2026-12-01',
      endDate: null,
      rent: 1395,
      depositRequired: 1395,
      status: 'active',
    },
    unitLabel: '#1A',
    propertyAddress: '412 Oakmont Ave',
  };
  const PROPERTIES = {
    items: [
      {
        id: 'p2',
        address: '9 Cardinal Ct',
        city: null,
        ownerId: 'o2',
        ownerName: 'Pryor',
        units: 0,
        occupied: 0,
      },
      {
        id: 'p1',
        address: '412 Oakmont Ave',
        city: null,
        ownerId: 'o1',
        ownerName: 'Hargrove',
        units: 5,
        occupied: 2,
      },
    ],
    total: 2,
    page: 1,
    pageSize: 200,
  };
  const unit = (
    id: string,
    label: string,
    occupancy: string,
    availability: string,
    rent = 1295,
  ) => ({ id, propertyId: 'p1', label, rent, occupancy, availability });
  // In label order, as the server sends them.
  const UNITS = [
    unit('u1', '#1A', 'vacant', 'available', 1350),
    unit('u2', '#2B', 'occupied', 'available'),
    unit('u3', '#3C', 'vacant', 'unavailable'),
    unit('u4', '#4D', 'occupied', 'unavailable'),
    unit('u5', '#5E', 'vacant', 'available', 1425),
  ];

  interface Serve {
    properties?: () => Response | Promise<Response>;
    units?: (propertyId: string) => Response | Promise<Response>;
    refuse?: { status: number; body: object };
  }

  /** Serves the tenant (with no lease until one is created), the pickers' reads and the create. */
  function serveLease({ properties, units, refuse }: Serve = {}) {
    const sent: unknown[] = [];
    let created = false;
    server.use(
      http.get('/api/directory/tenants/t1', () => HttpResponse.json(created ? LEASED : NO_LEASE)),
      ledgerHandler([]),
      http.get('/api/directory/properties', () => properties?.() ?? HttpResponse.json(PROPERTIES)),
      http.get('/api/directory/units', ({ request }) => {
        const propertyId = new URL(request.url).searchParams.get('propertyId') ?? '';
        return units?.(propertyId) ?? HttpResponse.json(propertyId === 'p1' ? UNITS : []);
      }),
      http.post('/api/directory/leases', async ({ request }) => {
        sent.push(await request.json());
        if (refuse) return HttpResponse.json(refuse.body, { status: refuse.status });
        created = true;
        return HttpResponse.json({ id: 'l9' });
      }),
    );
    return sent;
  }

  /** A response held until `release` is called, so a test can see the read still loading. */
  function held() {
    let release = () => {};
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    return { gate, release: () => release() };
  }

  async function openAddLease() {
    const open = await screen.findByRole('button', { name: 'Add lease' });
    open.focus();
    await userEvent.keyboard('{Enter}');
    return screen.getByRole('dialog', { name: 'Add a lease for Jasmine Carter' });
  }

  const optionTexts = (select: HTMLElement) =>
    within(select)
      .getAllByRole('option')
      .map((option) => option.textContent);

  async function chooseUnit(dialog: HTMLElement, unitId: string) {
    await vi.waitFor(() =>
      expect(within(dialog).getByRole('option', { name: '412 Oakmont Ave' })).toBeInTheDocument(),
    );
    await userEvent.selectOptions(within(dialog).getByLabelText('Property'), 'p1');
    await vi.waitFor(() =>
      expect(within(dialog).getByRole('option', { name: '#1A · vacant' })).toBeInTheDocument(),
    );
    await userEvent.selectOptions(within(dialog).getByLabelText('Unit'), unitId);
  }

  it('adds a lease by keyboard: the units list vacant and available first, lease rent prefills, and the header shows it', async () => {
    const sent = serveLease();
    renderLedger();

    const dialog = await openAddLease();
    const property = within(dialog).getByLabelText('Property');
    await vi.waitFor(() =>
      expect(optionTexts(property)).toEqual([
        'Choose a property',
        '412 Oakmont Ave',
        '9 Cardinal Ct',
      ]),
    );
    await userEvent.selectOptions(property, 'p1');

    const unitPicker = within(dialog).getByLabelText('Unit');
    await vi.waitFor(() =>
      expect(optionTexts(unitPicker)).toEqual([
        'Choose a unit',
        '#1A · vacant',
        '#5E · vacant',
        '#2B · occupied',
        '#3C · vacant · unavailable',
        '#4D · occupied · unavailable',
      ]),
    );
    await userEvent.selectOptions(unitPicker, 'u1');

    const rent = within(dialog).getByLabelText('Lease rent');
    expect(rent).toHaveValue(1350);
    await userEvent.clear(rent);
    await userEvent.type(rent, '1395');
    await userEvent.type(within(dialog).getByLabelText('Start date'), '2026-12-01');
    await userEvent.type(within(dialog).getByLabelText('Deposit required'), '1395{Enter}');

    await vi.waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(sent).toEqual([
      {
        tenantId: 't1',
        unitId: 'u1',
        startDate: '2026-12-01',
        endDate: null,
        rent: 1395,
        depositRequired: 1395,
        status: 'active',
      },
    ]);
    expect(await screen.findByText('Lease 2026-12-01 – —')).toBeVisible();
    expect(screen.getByText('#1A')).toBeVisible();
  });

  it('prefills lease rent again when another unit is chosen, and sends an end date and pending', async () => {
    const sent = serveLease();
    renderLedger();

    const dialog = await openAddLease();
    await chooseUnit(dialog, 'u1');
    await userEvent.selectOptions(within(dialog).getByLabelText('Unit'), 'u5');
    expect(within(dialog).getByLabelText('Lease rent')).toHaveValue(1425);

    await userEvent.type(within(dialog).getByLabelText('Start date'), '2027-01-01');
    await userEvent.type(within(dialog).getByLabelText('End date'), '2027-12-31');
    await userEvent.type(within(dialog).getByLabelText('Deposit required'), '0');
    await userEvent.selectOptions(within(dialog).getByLabelText('Lifecycle'), 'pending');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add lease' }));

    await vi.waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]).toMatchObject({
      unitId: 'u5',
      startDate: '2027-01-01',
      endDate: '2027-12-31',
      rent: 1425,
      depositRequired: 0,
      status: 'pending',
    });
  });

  it('asks for a property, a unit, a start date, lease rent and deposit before anything is sent, and offers only active or pending', async () => {
    const sent = serveLease();
    renderLedger();

    const dialog = await openAddLease();
    for (const name of ['Property', 'Unit', 'Start date', 'Lease rent', 'Deposit required']) {
      expect(within(dialog).getByLabelText(name)).toBeRequired();
    }
    expect(within(dialog).getByLabelText('End date')).not.toBeRequired();
    for (const name of ['Lease rent', 'Deposit required']) {
      expect(within(dialog).getByLabelText(name)).toHaveAttribute('min', '0');
      expect(within(dialog).getByLabelText(name)).toHaveAttribute('step', '0.01');
    }
    const status = within(dialog).getByLabelText('Lifecycle');
    expect(status).toHaveValue('active');
    expect(optionTexts(status)).toEqual(['Active', 'Pending']);
    // The header shows only the lease in effect today, so a later or pending one would seem lost.
    expect(
      within(dialog).getByText(
        /a pending lease, or one that starts later, shows here once it is in effect/i,
      ),
    ).toBeVisible();
    // Late-fee overrides are set afterwards with Late fees.
    expect(within(dialog).queryByLabelText(/late fee/i)).toBeNull();

    await userEvent.click(within(dialog).getByRole('button', { name: 'Add lease' }));
    expect(screen.getByRole('dialog')).toBeVisible();
    expect(sent).toEqual([]);
  });

  it('shows the server’s refusal of an overlapping term with its reference, keeps the dialog open and creates nothing', async () => {
    const detail = 'A tenant cannot have overlapping non-pending lease terms.';
    const sent = serveLease({
      refuse: {
        status: 400,
        body: { code: 'validation_failed', detail, correlationId: 'corr-781' },
      },
    });
    renderLedger();

    const dialog = await openAddLease();
    await chooseUnit(dialog, 'u1');
    await userEvent.type(within(dialog).getByLabelText('Start date'), '2026-12-01');
    await userEvent.type(within(dialog).getByLabelText('Deposit required'), '1350{Enter}');

    expect(await within(dialog).findByText(new RegExp(detail))).toBeVisible();
    expect(within(dialog).getByText(/Reference: corr-781/)).toBeVisible();
    expect(sent).toHaveLength(1);
    expect(screen.getByRole('dialog')).toBeVisible();
    expect(screen.queryByText(/^Lease \d/)).toBeNull();
  });

  it('says while the properties load, and offers a retry when they can’t be read', async () => {
    let fail = true;
    const first = held();
    serveLease({
      properties: async () => {
        if (!fail) return HttpResponse.json(PROPERTIES);
        await first.gate;
        return HttpResponse.json(
          { detail: 'Properties unavailable.', correlationId: 'corr-p' },
          { status: 500 },
        );
      },
    });
    renderLedger();

    const dialog = await openAddLease();
    expect(within(dialog).getByText('Loading the properties…')).toBeVisible();
    first.release();
    expect(await within(dialog).findByText(/Reference: corr-p/)).toBeVisible();
    expect(within(dialog).queryByLabelText('Unit')).toBeNull();
    fail = false;
    await userEvent.click(within(dialog).getByRole('button', { name: /retry/i }));
    expect(
      await within(dialog).findByRole('option', { name: '412 Oakmont Ave' }),
    ).toBeInTheDocument();
  });

  it('says when there is no property to lease in', async () => {
    serveLease({ properties: () => HttpResponse.json({ ...PROPERTIES, items: [], total: 0 }) });
    renderLedger();

    const dialog = await openAddLease();
    expect(await within(dialog).findByText(/No properties yet/)).toBeVisible();
    // With nothing to lease in there is nothing to save, and the dialog says why rather than
    // ignoring the button.
    expect(within(dialog).getByRole('button', { name: 'Add lease' })).toBeDisabled();
  });

  it('sends nothing while the chosen property has no unit to pick, and forgets the last unit’s rent', async () => {
    const sent = serveLease();
    renderLedger();

    const dialog = await openAddLease();
    await chooseUnit(dialog, 'u1');
    expect(within(dialog).getByLabelText('Lease rent')).toHaveValue(1350);
    await userEvent.selectOptions(within(dialog).getByLabelText('Property'), 'p2');
    expect(await within(dialog).findByText(/9 Cardinal Ct has no units yet/)).toBeVisible();
    // The rent was 1350's unit's, and that unit is no longer chosen.
    expect(within(dialog).getByLabelText('Lease rent')).toHaveValue(null);

    await userEvent.type(within(dialog).getByLabelText('Lease rent'), '1200');
    await userEvent.type(within(dialog).getByLabelText('Start date'), '2026-12-01');
    await userEvent.type(within(dialog).getByLabelText('Deposit required'), '0{Enter}');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add lease' }));

    // The unit is still required, so the form stops before anything is sent.
    expect(within(dialog).getByLabelText('Unit')).toBeInvalid();
    expect(screen.getByRole('dialog')).toBeVisible();
    expect(sent).toEqual([]);
  });

  it('says while a property’s units load, when it has none, and offers a retry when they can’t be read', async () => {
    let fail = true;
    const first = held();
    serveLease({
      units: async (propertyId) => {
        if (propertyId !== 'p1') return HttpResponse.json([]);
        if (!fail) return HttpResponse.json(UNITS);
        await first.gate;
        return HttpResponse.json(
          { detail: 'Units unavailable.', correlationId: 'corr-u' },
          { status: 500 },
        );
      },
    });
    renderLedger();

    const dialog = await openAddLease();
    await vi.waitFor(() =>
      expect(within(dialog).getByRole('option', { name: '9 Cardinal Ct' })).toBeInTheDocument(),
    );
    await userEvent.selectOptions(within(dialog).getByLabelText('Property'), 'p2');
    expect(await within(dialog).findByText(/9 Cardinal Ct has no units yet/)).toBeVisible();

    await userEvent.selectOptions(within(dialog).getByLabelText('Property'), 'p1');
    expect(within(dialog).getByText('Loading the units…')).toBeVisible();
    first.release();
    expect(await within(dialog).findByText(/Reference: corr-u/)).toBeVisible();
    fail = false;
    await userEvent.click(within(dialog).getByRole('button', { name: /retry/i }));
    expect(await within(dialog).findByRole('option', { name: '#1A · vacant' })).toBeInTheDocument();
  });
});

describe('LedgerPage edit the tenant (#783)', () => {
  interface TenantSent {
    id: string;
    displayName: string;
    contactEmail: string | null;
    contactPhone: string | null;
    lifecycleStatus: string;
  }

  /**
   * Serves the tenant and records each save. A successful save changes what the next tenant read
   * returns, as the server does.
   */
  function serveEdit(
    options: {
      detail?: typeof DETAIL | object;
      refuse?: { status: number; body: object };
      /** Holds the save's answer until it resolves, so a test can see the dialog while it saves. */
      hold?: Promise<void>;
    } = {},
  ) {
    const sent: TenantSent[] = [];
    let detail = { ...(options.detail ?? DETAIL) } as typeof DETAIL;
    server.use(
      http.get('/api/directory/tenants/t1', () => HttpResponse.json(detail)),
      ledgerHandler([]),
      http.put('/api/directory/tenants/:id', async ({ request, params }) => {
        const body = (await request.json()) as TenantSent;
        sent.push({ ...body, id: String(params.id) });
        await options.hold;
        if (options.refuse)
          return HttpResponse.json(options.refuse.body, {
            status: options.refuse.status,
            headers: { 'content-type': 'application/problem+json' },
          });
        detail = {
          ...detail,
          displayName: body.displayName,
          contact: { email: body.contactEmail, phone: body.contactPhone },
          lifecycleStatus: body.lifecycleStatus,
        } as typeof DETAIL;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    return sent;
  }

  async function openEdit() {
    const open = await screen.findByRole('button', { name: 'Edit' });
    open.focus();
    await userEvent.keyboard('{Enter}');
    return screen.getByRole('dialog', { name: 'Edit Jasmine Carter' });
  }

  it('edits by keyboard from the header, opening on the stored values, and the header shows the change', async () => {
    const sent = serveEdit();
    renderLedger();

    const dialog = await openEdit();
    const name = within(dialog).getByLabelText('Display name');
    expect(name).toHaveFocus();
    expect(name).toHaveValue('Jasmine Carter');
    expect(within(dialog).getByLabelText('Email')).toHaveValue('');
    expect(within(dialog).getByLabelText('Phone')).toHaveValue('828-555-0100');
    const lifecycle = within(dialog).getByLabelText('Lifecycle');
    expect(lifecycle).toHaveValue('current');
    expect(
      within(lifecycle)
        .getAllByRole('option')
        .map((option) => option.textContent),
    ).toEqual(['Current', 'Evicting', 'Past']);

    await userEvent.clear(name);
    await userEvent.type(name, 'Jasmine Carter-Ode');
    await userEvent.type(within(dialog).getByLabelText('Email'), 'jasmine@example.test');
    await userEvent.selectOptions(lifecycle, 'evicting');
    await userEvent.type(within(dialog).getByLabelText('Phone'), '{Enter}');

    await vi.waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(sent).toEqual([
      {
        id: 't1',
        displayName: 'Jasmine Carter-Ode',
        contactEmail: 'jasmine@example.test',
        contactPhone: '828-555-0100',
        lifecycleStatus: 'evicting',
      },
    ]);
    expect(await screen.findByRole('heading', { name: 'Jasmine Carter-Ode' })).toBeVisible();
    expect(screen.getByText('Evicting')).toBeVisible();
  });

  it('says it is saving and takes no second save until the first is answered', async () => {
    let release = () => {};
    const sent = serveEdit({ hold: new Promise<void>((resolve) => (release = resolve)) });
    renderLedger();

    const dialog = await openEdit();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save tenant' }));
    const saving = await within(dialog).findByRole('button', { name: 'Saving…' });
    expect(saving).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText('Phone'), '{Enter}');
    expect(sent).toHaveLength(1);

    release();
    await vi.waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(sent).toHaveLength(1);
  });

  it('clears an emptied contact field instead of saving an empty string', async () => {
    const sent = serveEdit();
    renderLedger();

    const dialog = await openEdit();
    await userEvent.clear(within(dialog).getByLabelText('Phone'));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save tenant' }));

    await vi.waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]).toMatchObject({ contactEmail: null, contactPhone: null });
  });

  it('requires a display name and a well-formed email before anything is sent', async () => {
    const sent = serveEdit();
    renderLedger();

    const dialog = await openEdit();
    const name = within(dialog).getByLabelText('Display name');
    expect(name).toBeRequired();
    expect(name).toHaveAttribute('maxLength', '200');
    expect(within(dialog).getByLabelText('Email')).toHaveAttribute('type', 'email');

    await userEvent.clear(name);
    await userEvent.type(within(dialog).getByLabelText('Phone'), '{Enter}');
    expect((name as HTMLInputElement).validity.valueMissing).toBe(true);
    await userEvent.type(name, 'Jasmine Carter');
    const email = within(dialog).getByLabelText<HTMLInputElement>('Email');
    await userEvent.type(email, 'not-an-email{Enter}');
    expect(email.validity.typeMismatch).toBe(true);
    expect(screen.getByRole('dialog')).toBe(dialog);

    // Once the email is corrected, the next save is the only one the server ever receives.
    await userEvent.clear(email);
    await userEvent.type(email, 'jasmine@example.test{Enter}');
    await vi.waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(sent.map((body) => body.contactEmail)).toEqual(['jasmine@example.test']);
  });

  it('shows the server’s refusal with its support reference and keeps the dialog open', async () => {
    const detail = 'Lifecycle status must be one of: current, evicting, past.';
    serveEdit({
      refuse: {
        status: 400,
        body: { code: 'validation_failed', detail, correlationId: 'corr-783t' },
      },
    });
    renderLedger();

    const dialog = await openEdit();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save tenant' }));

    expect(await within(dialog).findByText(new RegExp(detail))).toBeVisible();
    expect(within(dialog).getByText(/Reference: corr-783t/)).toBeVisible();
    expect(screen.getByRole('dialog')).toBe(dialog);
  });

  it('offers Edit for a tenant with no lease, where there are no late fees to set', async () => {
    serveEdit({
      detail: { ...DETAIL, lease: null, unitLabel: null, propertyAddress: null, balance: 0 },
    });
    renderLedger();

    expect(await screen.findByRole('button', { name: 'Edit' })).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Late fees' })).toBeNull();
  });
});
