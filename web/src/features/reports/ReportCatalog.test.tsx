import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { server } from '@/test/mocks/server';
import { ReportCatalog } from './ReportCatalog';

const CATALOG = [
  {
    id: 'owner-bal',
    name: 'All owner ending balances',
    category: 'Owner',
    description: 'Every owner balance with per-bank breakdown',
    favorite: true,
    icon: 'dashboard',
    acceptedFilters: ['year', 'month'],
  },
  {
    id: 'trust-ledger',
    name: 'Trust account ledger',
    category: 'Trust accounting',
    description: 'Full activity for any trust account',
    favorite: false,
    icon: 'doc',
    acceptedFilters: ['year', 'month', 'bankAccountId'],
  },
  {
    id: 'bank-rec',
    name: 'Bank reconciliation',
    category: 'Banking',
    description: 'Reconciliation detail with cleared status',
    favorite: true,
    icon: 'bank',
    acceptedFilters: ['year', 'month', 'bankAccountId'],
  },
  {
    id: 'owner-stmt',
    name: 'Owner statement',
    category: 'Owner',
    description: 'Monthly statement for a specific owner',
    favorite: false,
    icon: 'owners',
    acceptedFilters: ['year', 'month', 'ownerId'],
  },
];

const OWNERS_RESPONSE = {
  items: [
    {
      id: 'owner-1',
      name: 'Helen Ford',
      initials: 'HF',
      properties: 2,
      units: 4,
      operating: 1200,
      deposits: 950,
      total: 2150,
    },
    {
      id: 'owner-2',
      name: 'Bob Smith',
      initials: 'BS',
      properties: 1,
      units: 1,
      operating: 800,
      deposits: 0,
      total: 800,
    },
  ],
  page: 1,
  pageSize: 200,
  total: 2,
};

const BANKS_RESPONSE = {
  rows: [
    {
      bankAccountId: 'bank-1',
      name: 'Main Trust',
      book: 5000,
      cleared: 4800,
      uncleared: 200,
      unclearedCount: 2,
    },
    {
      bankAccountId: 'bank-2',
      name: 'Security Deposit',
      book: 2000,
      cleared: 2000,
      uncleared: 0,
      unclearedCount: 0,
    },
  ],
};

const PREVIEW_RESPONSE = {
  columns: ['Owner', 'Operating', 'Deposit', 'Ending'],
  rows: [
    { Owner: 'Helen Ford', Operating: 1200, Deposit: 950, Ending: 2150 },
    { Owner: 'Bob Smith', Operating: 800, Deposit: 0, Ending: 800 },
  ],
  totalRows: 2,
};

function baseHandlers() {
  return [
    http.get('/api/reports', () => HttpResponse.json(CATALOG)),
    http.get('/api/reports/:id/preview', () => HttpResponse.json(PREVIEW_RESPONSE)),
    http.get('/api/directory/owners', () => HttpResponse.json(OWNERS_RESPONSE)),
    http.get('/api/directory/properties', () =>
      HttpResponse.json({ items: [], page: 1, pageSize: 200, total: 0 }),
    ),
    http.get('/api/accounting/banks/balances', () => HttpResponse.json(BANKS_RESPONSE)),
  ];
}

function renderCatalog() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <ReportCatalog />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  document.body.innerHTML = '';
  vi.clearAllMocks();
});

describe('ReportCatalog', () => {
  it('renders catalog loading skeleton then shows report cards', async () => {
    server.use(...baseHandlers());
    renderCatalog();

    // Should show heading immediately (the skeleton includes it).
    expect(screen.getByRole('heading', { name: 'Reports' })).toBeInTheDocument();

    // Reports appear after load. Use getAllBy since the selected report name also appears in the builder.
    expect(await screen.findAllByText('All owner ending balances')).not.toHaveLength(0);
    expect(screen.getAllByText('Trust account ledger').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Bank reconciliation').length).toBeGreaterThan(0);
  });

  it('marks favorite reports with a ★ badge', async () => {
    server.use(...baseHandlers());
    renderCatalog();

    await screen.findAllByText('All owner ending balances');
    const list = screen.getByRole('list', { name: 'Available reports' });
    // Two favorites: owner-bal and bank-rec.
    expect(within(list).getAllByText('★').length).toBe(2);
  });

  it('renders category tabs and filters the report list', async () => {
    server.use(...baseHandlers());
    renderCatalog();

    await screen.findAllByText('All owner ending balances');

    // Click "Banking" tab.
    const tabList = screen.getByRole('tablist');
    await userEvent.click(within(tabList).getByRole('tab', { name: 'Banking' }));

    // Only the banking report is visible in the list.
    const list = screen.getByRole('list', { name: 'Available reports' });
    expect(within(list).getAllByText('Bank reconciliation').length).toBeGreaterThan(0);
    expect(within(list).queryByText('All owner ending balances')).not.toBeInTheDocument();
    expect(within(list).queryByText('Trust account ledger')).not.toBeInTheDocument();
  });

  it('loads and renders the live preview for the auto-selected report', async () => {
    server.use(...baseHandlers());
    renderCatalog();

    // Wait for catalog to appear.
    await screen.findAllByText('All owner ending balances');

    // Wait for preview to load (the first report is auto-selected).
    const table = await screen.findByRole('table', { name: 'Report preview' });
    expect(within(table).getByText('Helen Ford')).toBeInTheDocument();
    expect(within(table).getByText('Bob Smith')).toBeInTheDocument();
  });

  it('shows the correct preview when switching reports', async () => {
    const previewCalls: string[] = [];
    server.use(
      http.get('/api/reports', () => HttpResponse.json(CATALOG)),
      http.get('/api/reports/:id/preview', ({ params }) => {
        previewCalls.push(params.id as string);
        return HttpResponse.json(PREVIEW_RESPONSE);
      }),
    );
    renderCatalog();

    await screen.findAllByText('All owner ending balances');

    // Click "Trust account ledger".
    const list = screen.getByRole('list', { name: 'Available reports' });
    await userEvent.click(within(list).getByRole('button', { name: /Trust account ledger/ }));

    // A preview request for trust-ledger should follow.
    await vi.waitFor(() => expect(previewCalls.some((id) => id === 'trust-ledger')).toBe(true));
  });

  it('shows the empty state when the catalog returns zero reports', async () => {
    server.use(http.get('/api/reports', () => HttpResponse.json([])));
    renderCatalog();
    expect(await screen.findByText('No reports available')).toBeInTheDocument();
  });

  it('shows the error state when the catalog call fails', async () => {
    server.use(http.get('/api/reports', () => new HttpResponse(null, { status: 500 })));
    renderCatalog();
    expect(await screen.findByText("Couldn't load reports")).toBeInTheDocument();
  });

  it('filters by search text', async () => {
    server.use(...baseHandlers());
    renderCatalog();

    await screen.findAllByText('All owner ending balances');

    const searchInput = screen.getByRole('textbox', { name: 'Search reports' });
    await userEvent.type(searchInput, 'ledger');

    const list = screen.getByRole('list', { name: 'Available reports' });
    expect(within(list).getAllByText('Trust account ledger').length).toBeGreaterThan(0);
    expect(within(list).queryByText('All owner ending balances')).not.toBeInTheDocument();
  });

  it('shows the preview loading skeleton while preview is fetching', async () => {
    // Delay the preview response so we can see the skeleton.
    let resolvePreview!: () => void;
    const previewPending = new Promise<void>((res) => {
      resolvePreview = res;
    });
    server.use(
      http.get('/api/reports', () => HttpResponse.json(CATALOG)),
      http.get('/api/reports/:id/preview', async () => {
        await previewPending;
        return HttpResponse.json(PREVIEW_RESPONSE);
      }),
    );
    renderCatalog();

    // Reports load.
    await screen.findAllByText('All owner ending balances');

    // Preview section header is visible even during load.
    expect(screen.getByText('Live preview')).toBeInTheDocument();

    // Resolve the preview.
    resolvePreview();
    expect(await screen.findByRole('table', { name: 'Report preview' })).toBeInTheDocument();
  });

  it('shows basis toggle on reports that accept basis filter', async () => {
    server.use(...baseHandlers());
    renderCatalog();

    await screen.findAllByText('All owner ending balances');

    // owner-bal is auto-selected — it is BASIS_SENSITIVE so the toggle should appear.
    expect(screen.getByLabelText('Accounting basis')).toBeInTheDocument();
  });

  it('re-queries preview with new period params after changing the month', async () => {
    const previewRequests: URLSearchParams[] = [];
    server.use(
      http.get('/api/reports', () => HttpResponse.json(CATALOG)),
      http.get('/api/reports/:id/preview', ({ request }) => {
        previewRequests.push(new URL(request.url).searchParams);
        return HttpResponse.json(PREVIEW_RESPONSE);
      }),
      http.get('/api/directory/owners', () => HttpResponse.json(OWNERS_RESPONSE)),
      http.get('/api/directory/properties', () =>
        HttpResponse.json({ items: [], page: 1, pageSize: 200, total: 0 }),
      ),
      http.get('/api/accounting/banks/balances', () => HttpResponse.json(BANKS_RESPONSE)),
    );
    renderCatalog();

    // Wait for catalog + first preview.
    await screen.findAllByText('All owner ending balances');
    await screen.findByRole('table', { name: 'Report preview' });

    // Open the period filter chip and click a different month (Jan is index 0 = month 1).
    const filtersGroup = screen.getByRole('group', { name: 'Report filters' });
    // Click the Period chip to open the popover.
    const periodChip = within(filtersGroup).getByText('Period').closest('button')!;
    await userEvent.click(periodChip);

    // Pick January (first month in the grid).
    const popover = screen.getByRole('dialog', { name: 'Select period' });
    await userEvent.click(within(popover).getByRole('button', { name: 'Jan' }));

    // The dialog closes and a new preview request fires with month=1.
    await vi.waitFor(() => {
      const lastReq = previewRequests.at(-1);
      expect(lastReq?.get('month')).toBe('1');
    });
  });

  it('re-queries preview with basis param when toggling Cash/Accrual', async () => {
    const previewRequests: URLSearchParams[] = [];
    server.use(
      http.get('/api/reports', () => HttpResponse.json(CATALOG)),
      http.get('/api/reports/:id/preview', ({ request }) => {
        previewRequests.push(new URL(request.url).searchParams);
        return HttpResponse.json(PREVIEW_RESPONSE);
      }),
      http.get('/api/directory/owners', () => HttpResponse.json(OWNERS_RESPONSE)),
      http.get('/api/directory/properties', () =>
        HttpResponse.json({ items: [], page: 1, pageSize: 200, total: 0 }),
      ),
      http.get('/api/accounting/banks/balances', () => HttpResponse.json(BANKS_RESPONSE)),
    );
    renderCatalog();

    await screen.findAllByText('All owner ending balances');
    await screen.findByRole('table', { name: 'Report preview' });

    // owner-bal is BASIS_SENSITIVE; click Accrual.
    const basisGroup = screen.getByLabelText('Accounting basis');
    await userEvent.click(within(basisGroup).getByRole('button', { name: 'Accrual' }));

    await vi.waitFor(() => {
      const accrualReq = previewRequests.find((p) => p.get('basis') === 'accrual');
      expect(accrualReq).toBeDefined();
    });
  });

  it('renders bank filter chip on reports with bankAccountId in acceptedFilters', async () => {
    server.use(...baseHandlers());
    renderCatalog();

    await screen.findAllByText('All owner ending balances');

    // Select trust-ledger which has bankAccountId in acceptedFilters.
    const list = screen.getByRole('list', { name: 'Available reports' });
    await userEvent.click(within(list).getByRole('button', { name: /Trust account ledger/ }));

    // The Bank chip should appear.
    const filtersGroup = screen.getByRole('group', { name: 'Report filters' });
    expect(within(filtersGroup).getByText('Bank')).toBeInTheDocument();
  });

  it('re-queries preview with bankAccountId when a bank is selected', async () => {
    const previewRequests: URLSearchParams[] = [];
    server.use(
      http.get('/api/reports', () => HttpResponse.json(CATALOG)),
      http.get('/api/reports/:id/preview', ({ request }) => {
        previewRequests.push(new URL(request.url).searchParams);
        return HttpResponse.json(PREVIEW_RESPONSE);
      }),
      http.get('/api/directory/owners', () => HttpResponse.json(OWNERS_RESPONSE)),
      http.get('/api/directory/properties', () =>
        HttpResponse.json({ items: [], page: 1, pageSize: 200, total: 0 }),
      ),
      http.get('/api/accounting/banks/balances', () => HttpResponse.json(BANKS_RESPONSE)),
    );
    renderCatalog();

    await screen.findAllByText('All owner ending balances');

    // Switch to trust-ledger (has bankAccountId filter).
    const list = screen.getByRole('list', { name: 'Available reports' });
    await userEvent.click(within(list).getByRole('button', { name: /Trust account ledger/ }));
    await screen.findByRole('table', { name: 'Report preview' });

    // Open the Bank chip.
    const filtersGroup = screen.getByRole('group', { name: 'Report filters' });
    const bankChip = within(filtersGroup).getByText('Bank').closest('button')!;
    await userEvent.click(bankChip);

    // Select 'Main Trust'.
    const dialog = screen.getByRole('dialog', { name: 'Select Bank' });
    await userEvent.click(within(dialog).getByRole('button', { name: 'Main Trust' }));

    // A preview request should fire with bankAccountId=bank-1.
    await vi.waitFor(() => {
      const req = previewRequests.find((p) => p.get('bankAccountId') === 'bank-1');
      expect(req).toBeDefined();
    });
  });

  it('renders owner filter chip on reports with ownerId in acceptedFilters', async () => {
    server.use(...baseHandlers());
    renderCatalog();

    await screen.findAllByText('All owner ending balances');

    // Select owner-stmt which has ownerId in acceptedFilters.
    const list = screen.getByRole('list', { name: 'Available reports' });
    await userEvent.click(within(list).getByRole('button', { name: /Owner statement/ }));

    const filtersGroup = screen.getByRole('group', { name: 'Report filters' });
    expect(within(filtersGroup).getByText('Owner')).toBeInTheDocument();
  });

  // Finding 3: backend preview message is rendered in the empty state.
  it('renders backend message text in the empty-rows empty state (Finding 3)', async () => {
    const BACKEND_MESSAGE = 'No finalized reconciliation found for this bank account.';
    server.use(
      http.get('/api/reports', () => HttpResponse.json(CATALOG)),
      // Override preview to return empty rows WITH a backend message.
      http.get('/api/reports/:id/preview', () =>
        HttpResponse.json({
          columns: [],
          rows: [],
          totalRows: 0,
          message: BACKEND_MESSAGE,
        }),
      ),
    );
    renderCatalog();

    await screen.findAllByText('All owner ending balances');

    // The empty preview state must show the backend message, not the hardcoded fallback.
    expect(await screen.findByText(BACKEND_MESSAGE)).toBeInTheDocument();
    // The generic fallback must NOT appear.
    expect(screen.queryByText('No data for this period')).not.toBeInTheDocument();
  });
});
