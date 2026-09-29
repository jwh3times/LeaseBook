import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { getApiDashboard, unwrap } from '@/api';
import { sessionQueryKey } from '@/features/auth/useSession';
import { OwnerPortalPage } from '@/features/portal/OwnerPortalPage';
import { TenantPortalPage } from '@/features/portal/TenantPortalPage';
import { server } from '@/test/mocks/server';
import { HomeRedirect, PersonaGuard, type Persona } from './PersonaGuard';

// Stands in for the staff shell: one staff data hook, so a denied mount would show up as a request.
function StaffPage() {
  const dashboard = useQuery({
    queryKey: ['dashboard'],
    queryFn: () => unwrap(getApiDashboard(), 'Failed to load the dashboard.'),
  });
  return <h1>{dashboard.isSuccess ? 'Staff dashboard' : 'Staff loading'}</h1>;
}

const ROUTES: Record<Persona, { path: string; heading: string; dataPath: RegExp }> = {
  staff: { path: '/dashboard', heading: 'Staff dashboard', dataPath: /^\/api\/dashboard$/ },
  tenant: { path: '/portal/tenant', heading: 'Resident A', dataPath: /^\/api\/portal\/tenant\// },
  owner: { path: '/portal/owner', heading: 'Owner A', dataPath: /^\/api\/portal\/owner\// },
};

// Every request the page issues, recorded as it starts — whether or not a handler matches it, since
// MSW bypasses unstubbed requests silently.
let requested: string[] = [];
function record({ request }: { request: Request }) {
  requested.push(new URL(request.url).pathname);
}
beforeEach(() => {
  requested = [];
  server.events.on('request:start', record);
  server.use(
    http.get('/api/dashboard', () => HttpResponse.json({ asOf: '2026-09-29' })),
    http.get('/api/portal/tenant/ledger', () =>
      HttpResponse.json({ residentName: 'Resident A', balance: 0, rows: [] }),
    ),
    http.get('/api/portal/owner/summary', () =>
      HttpResponse.json({
        ownerName: 'Owner A',
        balance: 0,
        basis: 'cash',
        disbursements: [],
        activity: [],
      }),
    ),
    http.get('/api/portal/owner/statements', () => HttpResponse.json({ statements: [] })),
  );
});
afterEach(() => {
  server.events.removeListener('request:start', record);
});

function visit(role: string, path: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  queryClient.setQueryData(sessionQueryKey, { role });
  const router = createMemoryRouter(
    [
      {
        element: <PersonaGuard persona="tenant" />,
        children: [{ path: ROUTES.tenant.path, element: <TenantPortalPage /> }],
      },
      {
        element: <PersonaGuard persona="owner" />,
        children: [{ path: ROUTES.owner.path, element: <OwnerPortalPage /> }],
      },
      {
        element: <PersonaGuard persona="staff" />,
        children: [{ path: ROUTES.staff.path, element: <StaffPage /> }],
      },
    ],
    { initialEntries: [path] },
  );
  render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

const HOME: Record<string, Persona> = {
  PMAdmin: 'staff',
  PMStaff: 'staff',
  Tenant: 'tenant',
  Owner: 'owner',
};

const MATRIX = Object.entries(HOME).flatMap(([role, own]) =>
  (Object.keys(ROUTES) as Persona[]).map((target) => [role, target, own === target] as const),
);

describe('persona routing matrix', () => {
  it.each(MATRIX.filter(([, , allowed]) => allowed))(
    '%s reaches its own %s pages and loads their data',
    async (role, target) => {
      visit(role, ROUTES[target].path);
      expect(
        await screen.findByRole('heading', { name: ROUTES[target].heading }),
      ).toBeInTheDocument();
      expect(requested.some((path) => ROUTES[target].dataPath.test(path))).toBe(true);
    },
  );

  it.each(MATRIX.filter(([, , allowed]) => !allowed))(
    'denies %s on %s pages without requesting their data',
    async (role, target) => {
      visit(role, ROUTES[target].path);
      expect(await screen.findByRole('heading', { name: 'Access denied' })).toBeInTheDocument();
      expect(screen.getByRole('link', { name: 'Return to your account' })).toHaveAttribute(
        'href',
        ROUTES[HOME[role] as Persona].path,
      );
      // Give any hook that did mount a turn to fire before asserting silence.
      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(requested).toEqual([]);
    },
  );
});

it.each([
  ['Tenant', '/portal/tenant'],
  ['Owner', '/portal/owner'],
  ['PMAdmin', '/dashboard'],
  ['PMStaff', '/dashboard'],
  [null, '/account/security'],
])('routes %s root navigation to %s', async (role, destination) => {
  const queryClient = new QueryClient();
  queryClient.setQueryData(sessionQueryKey, role === null ? null : { role });
  const router = createMemoryRouter([
    { path: '/', element: <HomeRedirect /> },
    { path: destination, element: <div>Destination</div> },
  ]);
  render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  await screen.findByText('Destination');
  expect(router.state.location.pathname).toBe(destination);
});
