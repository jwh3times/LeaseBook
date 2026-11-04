import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import { http, HttpResponse, type JsonBodyType } from 'msw';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { useSearch } from './search';
import {
  useCreateOwner,
  useCreateProperty,
  useCreateTenant,
  useOwnerDetail,
  useOwners,
  useProperties,
  useTenants,
} from './directory';

/** How many times each read was fetched, by path. */
let reads: Record<string, number>;

const page = { items: [], total: 0, page: 1, pageSize: 200 };

/** Answers a read at `url` with `body`, counting each fetch under `name`. */
function read(name: string, url: string, body: JsonBodyType) {
  return http.get(url, () => {
    reads[name] = (reads[name] ?? 0) + 1;
    return HttpResponse.json(body);
  });
}

beforeEach(() => {
  reads = {};
  server.use(
    read('owners', '/api/directory/owners', page),
    read('owner o1', '/api/directory/owners/o1', { id: 'o1', name: 'Hargrove Family Trust' }),
    read('owner o2', '/api/directory/owners/o2', { id: 'o2', name: 'Pryor Holdings' }),
    read('properties', '/api/directory/properties', page),
    read('tenants', '/api/directory/tenants', page),
    read('search', '/api/search', []),
    http.post('/api/directory/properties', () => HttpResponse.json({ id: 'pNew' })),
    http.post('/api/directory/owners', () => HttpResponse.json({ id: 'oNew' })),
    http.post('/api/directory/tenants', () => HttpResponse.json({ id: 'tNew' })),
  );
});

/**
 * Mounts every directory read a create can change, plus `create`, on one client, and waits for the
 * first fetch of each. The reads stay mounted, so an invalidated one refetches at once, as it would
 * on a page that shows it.
 */
async function mountWithReads<T>(create: () => T) {
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
      tenants: useTenants(),
      search: useSearch('Har'),
      create: create(),
    }),
    { wrapper },
  );
  await waitFor(() =>
    expect(reads).toEqual({
      owners: 1,
      'owner o1': 1,
      'owner o2': 1,
      properties: 1,
      tenants: 1,
      search: 1,
    }),
  );
  return result;
}

// #559: a create refreshes every read whose result it changes, so nothing waits out the stale time.
describe('directory creates', () => {
  it("creating a property refetches its owner's list row and page, the properties and search", async () => {
    const result = await mountWithReads(useCreateProperty);
    await result.current.create.mutateAsync({
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
    await result.current.create.mutateAsync({
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
    await result.current.create.mutateAsync({
      displayName: 'New Renter',
      contactEmail: null,
      contactPhone: null,
      lifecycleStatus: 'current',
    });

    await waitFor(() => expect(reads).toMatchObject({ tenants: 2, search: 2 }));
    expect(reads).toMatchObject({ owners: 1, 'owner o1': 1, properties: 1 });
  });
});
