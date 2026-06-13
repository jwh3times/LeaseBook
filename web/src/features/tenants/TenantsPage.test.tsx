import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { beforeEach, describe, expect, it } from 'vitest';
import { RecordNavProvider } from '@/lib/recordNav';
import { server } from '@/test/mocks/server';
import { LedgerPage } from './LedgerPage';
import { TenantsPage } from './TenantsPage';

const TENANTS = [
  { id: 't1', displayName: 'Jasmine Carter', unitLabel: '#2B', rent: 1450, balance: 1450, status: 'current' },
  { id: 't2', displayName: 'Devon Pryor', unitLabel: '#1A', rent: 1380, balance: 0, status: 'current' },
  { id: 't3', displayName: 'Aisha Bello', unitLabel: '#3', rent: 1620, balance: 1620, status: 'late' },
];

function listHandler(items = TENANTS) {
  return http.get('/api/directory/tenants', () =>
    HttpResponse.json({ items, total: items.length, page: 1, pageSize: 200 }),
  );
}

function renderTenants() {
  // The detail route is the M3 LedgerPage, which loads the tenant's ledger; a benign empty ledger keeps
  // these navigation tests focused on routing.
  server.use(
    http.get('/api/accounting/tenants/:tenantId/ledger', ({ params }) =>
      HttpResponse.json({ tenantId: params.tenantId, balance: 0, rows: [] }),
    ),
  );
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      { path: '/tenants', element: <TenantsPage /> },
      { path: '/tenants/:id', element: <LedgerPage /> },
    ],
    { initialEntries: ['/tenants'] },
  );
  render(
    <QueryClientProvider client={queryClient}>
      <RecordNavProvider>
        <RouterProvider router={router} />
      </RecordNavProvider>
    </QueryClientProvider>,
  );
}

describe('TenantsPage', () => {
  it('renders rows from the payload', async () => {
    server.use(listHandler());
    renderTenants();
    expect(await screen.findByText('Jasmine Carter')).toBeInTheDocument();
    expect(screen.getByText('Devon Pryor')).toBeInTheDocument();
    expect(screen.getByText('Aisha Bello')).toBeInTheDocument();
  });

  it('filters the loaded page client-side', async () => {
    server.use(listHandler());
    renderTenants();
    await screen.findByText('Jasmine Carter');
    await userEvent.type(screen.getByLabelText('Filter tenants…'), 'bello');
    expect(screen.getByText('Aisha Bello')).toBeInTheDocument();
    expect(screen.queryByText('Jasmine Carter')).not.toBeInTheDocument();
  });

  it('navigates to the tenant on row click', async () => {
    server.use(
      listHandler(),
      http.get('/api/directory/tenants/t1', () =>
        HttpResponse.json({
          id: 't1', displayName: 'Jasmine Carter', contact: { email: null, phone: null }, status: 'current',
          lease: null, unitLabel: '#2B', propertyAddress: '412 Oakmont Ave', ownerId: 'o1', ownerName: 'Hargrove',
          balance: 1450, depositHeld: 1450,
        }),
      ),
    );
    renderTenants();
    await userEvent.click(await screen.findByText('Jasmine Carter'));
    // The list unmounts on navigation; the LedgerPage header renders the tenant name as a heading.
    expect(await screen.findByRole('heading', { name: 'Jasmine Carter' })).toBeInTheDocument();
  });

  it('selects with the keyboard and opens on Enter', async () => {
    server.use(
      listHandler(),
      http.get('/api/directory/tenants/t2', () =>
        HttpResponse.json({
          id: 't2', displayName: 'Devon Pryor', contact: { email: null, phone: null }, status: 'current',
          lease: null, unitLabel: '#1A', propertyAddress: '412 Oakmont Ave', ownerId: 'o1', ownerName: 'Hargrove',
          balance: 0, depositHeld: 0,
        }),
      ),
    );
    renderTenants();
    const search = await screen.findByLabelText('Filter tenants…');
    search.focus();
    await userEvent.keyboard('{ArrowDown}{Enter}'); // first ↓ selects index 1 (Devon Pryor)
    expect(await screen.findByRole('heading', { name: 'Devon Pryor' })).toBeInTheDocument();
  });

  it('creates a tenant and redirects to the new record', async () => {
    server.use(
      listHandler(),
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.post('/api/directory/tenants', () => HttpResponse.json({ id: 'tNew' })),
      http.get('/api/directory/tenants/tNew', () =>
        HttpResponse.json({
          id: 'tNew', displayName: 'New Renter', contact: { email: null, phone: null }, status: 'current',
          lease: null, unitLabel: null, propertyAddress: null, ownerId: null, ownerName: null, balance: 0, depositHeld: 0,
        }),
      ),
    );
    renderTenants();
    await userEvent.click(await screen.findByRole('button', { name: /new tenant/i }));
    await userEvent.type(screen.getByLabelText('Display name'), 'New Renter');
    await userEvent.click(screen.getByRole('button', { name: /create tenant/i }));
    expect(await screen.findByRole('heading', { name: 'New Renter' })).toBeInTheDocument();
  });

  it('shows an empty state with no tenants', async () => {
    server.use(listHandler([]));
    renderTenants();
    expect(await screen.findByText('No tenants yet')).toBeInTheDocument();
  });

  it('shows an error state when the list fails', async () => {
    server.use(http.get('/api/directory/tenants', () => new HttpResponse(null, { status: 500 })));
    renderTenants();
    expect(await screen.findByText(/couldn’t load this list/i)).toBeInTheDocument();
  });

  beforeEach(() => {
    document.body.innerHTML = '';
  });
});
