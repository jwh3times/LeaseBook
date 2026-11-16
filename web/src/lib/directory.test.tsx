import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import { http, HttpResponse, type JsonBodyType } from 'msw';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { useReportPreview } from '@/features/reports/reports';
import { useDashboard } from './dashboard';
import { useSearch } from './search';
import {
  useCreateLease,
  useCreateOwner,
  useCreateProperty,
  useCreateTenant,
  useCreateUnit,
  useOwnerDetail,
  useOwners,
  useProperties,
  usePropertyDetail,
  useTenantDetail,
  useTenants,
  useUnits,
  useUpdateUnit,
} from './directory';

/** How many times each read was fetched, by path. */
let reads: Record<string, number>;

const page = { items: [], total: 0, page: 1, pageSize: 200 };

/** Answers a read at `url` with `body`, counting each fetch under `name`. */
function countedRead(name: string, url: string, body: JsonBodyType) {
  return http.get(url, () => {
    reads[name] = (reads[name] ?? 0) + 1;
    return HttpResponse.json(body);
  });
}

beforeEach(() => {
  reads = {};
  server.use(
    countedRead('owners', '/api/directory/owners', page),
    countedRead('owner o1', '/api/directory/owners/o1', {
      id: 'o1',
      name: 'Hargrove Family Trust',
    }),
    countedRead('owner o2', '/api/directory/owners/o2', { id: 'o2', name: 'Pryor Holdings' }),
    countedRead('properties', '/api/directory/properties', page),
    countedRead('property p1', '/api/directory/properties/p1', { id: 'p1', ownerId: 'o1' }),
    countedRead('property p2', '/api/directory/properties/p2', { id: 'p2', ownerId: 'o2' }),
    countedRead('tenants', '/api/directory/tenants', page),
    countedRead('tenant t1', '/api/directory/tenants/t1', { id: 't1', displayName: 'Ana Ruiz' }),
    countedRead('search', '/api/search', []),
    countedRead('dashboard', '/api/dashboard', {}),
    // The units read names its property in the query string, so one handler counts each property's.
    http.get('/api/directory/units', ({ request }) => {
      const propertyId = new URL(request.url).searchParams.get('propertyId');
      reads[`units ${propertyId}`] = (reads[`units ${propertyId}`] ?? 0) + 1;
      return HttpResponse.json([]);
    }),
    countedRead('rent roll', '/api/reports/rent-roll/preview', { columns: [], rows: [] }),
    http.post('/api/directory/properties', () => HttpResponse.json({ id: 'pNew' })),
    http.post('/api/directory/owners', () => HttpResponse.json({ id: 'oNew' })),
    http.post('/api/directory/tenants', () => HttpResponse.json({ id: 'tNew' })),
    http.post('/api/directory/units', () => HttpResponse.json({ id: 'uNew' })),
    http.put('/api/directory/units/:id', () => new HttpResponse(null, { status: 204 })),
    http.post('/api/directory/leases', () => HttpResponse.json({ id: 'lNew' })),
  );
});

/**
 * Mounts every directory read a create can change, plus `create`, on one client, and waits for the
 * first fetch of each. The reads stay mounted, so an invalidated one refetches at once, as it would
 * on a page that shows it.
 */
async function mountWithReads<T>(mutate: () => T) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  const { result } = renderHook(
    () => ({
      owners: useOwners(),
      owner: useOwnerDetail('o1'),
      otherOwner: useOwnerDetail('o2'),
      properties: useProperties(),
      property: usePropertyDetail('p1'),
      otherProperty: usePropertyDetail('p2'),
      tenants: useTenants(),
      tenant: useTenantDetail('t1'),
      search: useSearch('Har'),
      dashboard: useDashboard(),
      units: useUnits('p1'),
      otherUnits: useUnits('p2'),
      rentRoll: useReportPreview('rent-roll', {}),
      mutation: mutate(),
    }),
    { wrapper },
  );
  await waitFor(() =>
    expect(reads).toEqual({
      owners: 1,
      'owner o1': 1,
      'owner o2': 1,
      properties: 1,
      'property p1': 1,
      'property p2': 1,
      tenants: 1,
      'tenant t1': 1,
      search: 1,
      dashboard: 1,
      'units p1': 1,
      'units p2': 1,
      'rent roll': 1,
    }),
  );
  return result;
}

// #559: a create refreshes every read whose result it changes, so nothing waits out the stale time.
describe('directory creates', () => {
  it("creating a property refetches its owner's list row and page, the properties and search", async () => {
    const result = await mountWithReads(useCreateProperty);
    await result.current.mutation.mutateAsync({
      ownerId: 'o1',
      address: '9 Cardinal Ct',
      city: null,
      state: null,
      zip: null,
      mgmtFeeBps: null,
    });

    await waitFor(() =>
      expect(reads).toMatchObject({ owners: 2, 'owner o1': 2, properties: 2, search: 2 }),
    );
    // Only the owner the property was created for has changed.
    expect(reads).toMatchObject({ 'owner o2': 1, tenants: 1 });
  });

  it('creating an owner refetches the owners and search', async () => {
    const result = await mountWithReads(useCreateOwner);
    await result.current.mutation.mutateAsync({
      name: 'New Owner',
      initials: null,
      contactEmail: null,
      contactPhone: null,
      defaultMgmtFeeBps: null,
      reserveAmount: 0,
    });

    await waitFor(() => expect(reads).toMatchObject({ owners: 2, search: 2 }));
    expect(reads).toMatchObject({ 'owner o1': 1, properties: 1, tenants: 1 });
  });

  it('creating a tenant refetches the tenants and search', async () => {
    const result = await mountWithReads(useCreateTenant);
    await result.current.mutation.mutateAsync({
      displayName: 'New Renter',
      contactEmail: null,
      contactPhone: null,
      lifecycleStatus: 'current',
    });

    await waitFor(() => expect(reads).toMatchObject({ tenants: 2, search: 2 }));
    expect(reads).toMatchObject({ owners: 1, 'owner o1': 1, properties: 1 });
  });
});

describe('unit writes', () => {
  const property = { id: 'p1', ownerId: 'o1' };

  it("adding a unit refetches its property, the property and owner unit counts, its owner's page and search", async () => {
    const result = await mountWithReads(() => useCreateUnit(property));
    await result.current.mutation.mutateAsync({
      propertyId: 'p1',
      label: '#3C',
      rent: 1525,
      availability: 'available',
    });

    await waitFor(() =>
      expect(reads).toMatchObject({
        'property p1': 2,
        // The add-lease picker lists the property's units.
        'units p1': 2,
        properties: 2,
        owners: 2,
        'owner o1': 2,
        search: 2,
        // A new unit has no lease in effect, so the dashboard's vacancy counts it.
        dashboard: 2,
      }),
    );
    // No other property or owner gained a unit, and no tenant's lease changed.
    expect(reads).toMatchObject({
      'property p2': 1,
      'units p2': 1,
      'owner o2': 1,
      tenants: 1,
      'tenant t1': 1,
    });
  });

  it('editing a unit refetches its property, every tenant read that shows its label, and search', async () => {
    const result = await mountWithReads(() => useUpdateUnit(property));
    await result.current.mutation.mutateAsync({
      id: 'u1',
      label: '#3C',
      rent: 1525,
      availability: 'unavailable',
    });

    await waitFor(() =>
      expect(reads).toMatchObject({
        'property p1': 2,
        'units p1': 2,
        tenants: 2,
        'tenant t1': 2,
        search: 2,
      }),
    );
    // A label, asking rent or availability change moves no unit count, and vacancy ignores
    // availability.
    expect(reads).toMatchObject({
      dashboard: 1,
      'property p2': 1,
      properties: 1,
      owners: 1,
      'owner o1': 1,
      'owner o2': 1,
    });
  });
});

describe('units read', () => {
  it('reads nothing until a property is chosen', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );
    const { result } = renderHook(() => ({ none: useUnits(''), chosen: useUnits('p1') }), {
      wrapper,
    });

    // By the time the chosen property's units have arrived, the unchosen read has sent nothing.
    await waitFor(() => expect(result.current.chosen.isSuccess).toBe(true));
    expect(result.current.none.fetchStatus).toBe('idle');
    expect(reads).toEqual({ 'units p1': 1 });
  });
});

describe('lease writes', () => {
  const property = { id: 'p1', ownerId: 'o1' };

  it("adding a lease refetches the tenant, its unit's occupancy everywhere it shows, the dashboard, search and the rent roll", async () => {
    const result = await mountWithReads(() => useCreateLease('t1'));
    await result.current.mutation.mutateAsync({
      property,
      lease: {
        tenantId: 't1',
        unitId: 'u1',
        startDate: '2026-11-15',
        endDate: null,
        rent: 1450,
        depositRequired: 1450,
        status: 'active',
      },
    });

    await waitFor(() =>
      expect(reads).toMatchObject({
        // The ledger header shows the lease, its unit and property.
        'tenant t1': 2,
        // The tenant row shows the unit label and lease rent.
        tenants: 2,
        // The unit is now occupied: on its property, in the picker, in the occupied count and on
        // its owner's property rows.
        'property p1': 2,
        'units p1': 2,
        properties: 2,
        'owner o1': 2,
        // Vacancy and scheduled rent.
        dashboard: 2,
        // A tenant's search sublabel is the unit of their lease.
        search: 2,
        'rent roll': 2,
      }),
    );
    // No other property or owner changed, and the owners list counts units, not leases.
    expect(reads).toMatchObject({
      'property p2': 1,
      'units p2': 1,
      'owner o2': 1,
      owners: 1,
    });
  });
});
