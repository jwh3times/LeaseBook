import { useMutation, useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import { api, primeCsrf, type components } from '@/api';

export type TenantListRow = components['schemas']['TenantListRow'];
export type OwnerListRow = components['schemas']['OwnerListRow'];
export type PropertyListRow = components['schemas']['PropertyListRow'];
export type UnitRow = components['schemas']['UnitRow'];
export type TenantDetail = components['schemas']['TenantDetail'];
export type OwnerDetail = components['schemas']['OwnerDetail'];
export type PropertyDetail = components['schemas']['PropertyDetail'];

// .NET 10's OpenAPI types decimals as JSON-Schema ["number","string"], so the generated client widens
// numeric fields to `number | string` even though they arrive as JSON numbers. Coerce at the render
// boundary so money/quantities reach `<Money>` and arithmetic as real numbers.
export const num = (value: number | string | null | undefined): number =>
  value == null ? 0 : typeof value === 'number' ? value : Number(value);

// At demo/Pro scale (≤ 300 units) the UI loads one ample page and filters client-side instantly (P42),
// so the hooks fetch a large first page and the screens narrow it in memory.
const PAGE = 200;

function unwrap<T>(result: { data?: T; error?: unknown }, what: string): T {
  if (result.error || result.data === undefined) {
    throw new Error(`Failed to load ${what}`);
  }
  return result.data;
}

export function useTenants(): UseQueryResult<components['schemas']['PagedResponseOfTenantListRow']> {
  return useQuery({
    queryKey: ['tenants'],
    queryFn: async () => unwrap(await api.GET('/api/directory/tenants', { params: { query: { pageSize: PAGE } } }), 'tenants'),
  });
}

export function useOwners(): UseQueryResult<components['schemas']['PagedResponseOfOwnerListRow']> {
  return useQuery({
    queryKey: ['owners'],
    queryFn: async () => unwrap(await api.GET('/api/directory/owners', { params: { query: { pageSize: PAGE } } }), 'owners'),
  });
}

export function useProperties(): UseQueryResult<components['schemas']['PagedResponseOfPropertyListRow']> {
  return useQuery({
    queryKey: ['properties'],
    queryFn: async () => unwrap(await api.GET('/api/directory/properties', { params: { query: { pageSize: PAGE } } }), 'properties'),
  });
}

export function useTenantDetail(id: string): UseQueryResult<TenantDetail> {
  return useQuery({
    queryKey: ['tenant', id],
    queryFn: async () => unwrap(await api.GET('/api/directory/tenants/{id}', { params: { path: { id } } }), 'tenant'),
  });
}

export function useOwnerDetail(id: string): UseQueryResult<OwnerDetail> {
  return useQuery({
    queryKey: ['owner', id],
    queryFn: async () => unwrap(await api.GET('/api/directory/owners/{id}', { params: { path: { id } } }), 'owner'),
  });
}

export function usePropertyDetail(id: string): UseQueryResult<PropertyDetail> {
  return useQuery({
    queryKey: ['property', id],
    queryFn: async () => unwrap(await api.GET('/api/directory/properties/{id}', { params: { path: { id } } }), 'property'),
  });
}

type CreateTenantBody = components['schemas']['CreateTenant'];
type CreateOwnerBody = components['schemas']['CreateOwner'];
type CreatePropertyBody = components['schemas']['CreateProperty'];

export function useCreateTenant() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (body: CreateTenantBody) => {
      await primeCsrf();
      return unwrap(await api.POST('/api/directory/tenants', { body }), 'tenant create');
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ['tenants'] }),
  });
}

export function useCreateOwner() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (body: CreateOwnerBody) => {
      await primeCsrf();
      return unwrap(await api.POST('/api/directory/owners', { body }), 'owner create');
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ['owners'] }),
  });
}

export function useCreateProperty() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (body: CreatePropertyBody) => {
      await primeCsrf();
      return unwrap(await api.POST('/api/directory/properties', { body }), 'property create');
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ['properties'] }),
  });
}
