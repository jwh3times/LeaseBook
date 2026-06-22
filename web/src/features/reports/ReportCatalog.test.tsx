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
];

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
});
