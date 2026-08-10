import { useMutation, useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import {
  getApiDirectoryOwners,
  getApiDirectoryOwnersById,
  getApiDirectoryProperties,
  getApiDirectoryPropertiesById,
  getApiDirectoryTenants,
  getApiDirectoryTenantsById,
  postApiDirectoryOwners,
  postApiDirectoryProperties,
  postApiDirectoryTenants,
  primeCsrf,
  putApiDirectoryLeasesById,
  type CreateOwner,
  type CreateProperty,
  type CreateTenant,
  type OwnerDetail,
  type OwnerListRow,
  type PagedResponseOfOwnerListRow,
  type PagedResponseOfPropertyListRow,
  type PagedResponseOfTenantListRow,
  type PropertyDetail,
  type PropertyListRow,
  type TenantDetail,
  type TenantListRow,
  type UnitRow,
  type UpdateLease,
} from '@/api';

export type {
  OwnerDetail,
  OwnerListRow,
  PropertyDetail,
  PropertyListRow,
  TenantDetail,
  TenantListRow,
  UnitRow,
};

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

export function useTenants(): UseQueryResult<PagedResponseOfTenantListRow> {
  return useQuery({
    queryKey: ['tenants'],
    queryFn: async () =>
      unwrap(await getApiDirectoryTenants({ query: { pageSize: PAGE } }), 'tenants'),
  });
}

export function useOwners(
  opts: { enabled?: boolean } = {},
): UseQueryResult<PagedResponseOfOwnerListRow> {
  return useQuery({
    queryKey: ['owners'],
    enabled: opts.enabled ?? true,
    queryFn: async () =>
      unwrap(await getApiDirectoryOwners({ query: { pageSize: PAGE } }), 'owners'),
  });
}

export function useProperties(
  opts: { enabled?: boolean } = {},
): UseQueryResult<PagedResponseOfPropertyListRow> {
  return useQuery({
    queryKey: ['properties'],
    enabled: opts.enabled ?? true,
    queryFn: async () =>
      unwrap(await getApiDirectoryProperties({ query: { pageSize: PAGE } }), 'properties'),
  });
}

export function useTenantDetail(id: string): UseQueryResult<TenantDetail> {
  return useQuery({
    queryKey: ['tenant', id],
    queryFn: async () => unwrap(await getApiDirectoryTenantsById({ path: { id } }), 'tenant'),
  });
}

export function useOwnerDetail(id: string): UseQueryResult<OwnerDetail> {
  return useQuery({
    queryKey: ['owner', id],
    queryFn: async () => unwrap(await getApiDirectoryOwnersById({ path: { id } }), 'owner'),
  });
}

export function usePropertyDetail(id: string): UseQueryResult<PropertyDetail> {
  return useQuery({
    queryKey: ['property', id],
    queryFn: async () => unwrap(await getApiDirectoryPropertiesById({ path: { id } }), 'property'),
  });
}

type CreateTenantBody = CreateTenant;
type CreateOwnerBody = CreateOwner;
type CreatePropertyBody = CreateProperty;

type UpdateLeaseBody = UpdateLease;

/**
 * Updates a lease (WP-6). `UpdateLease` replaces every field rather than patching, so callers must
 * send the lease back whole — the tenant-detail read returns everything needed for that round trip.
 * Invalidates the tenant detail so the header reflects the new policy immediately.
 */
export function useUpdateLease(tenantId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async ({ id, ...body }: UpdateLeaseBody & { id: string }) => {
      await primeCsrf();
      const { error } = await putApiDirectoryLeasesById({
        path: { id },
        body: body as UpdateLeaseBody,
      });
      if (error) throw error;
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ['tenant', tenantId] }),
  });
}

export function useCreateTenant() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (body: CreateTenantBody) => {
      await primeCsrf();
      return unwrap(await postApiDirectoryTenants({ body }), 'tenant create');
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ['tenants'] }),
  });
}

export function useCreateOwner() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (body: CreateOwnerBody) => {
      await primeCsrf();
      return unwrap(await postApiDirectoryOwners({ body }), 'owner create');
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ['owners'] }),
  });
}

export function useCreateProperty() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (body: CreatePropertyBody) => {
      await primeCsrf();
      return unwrap(await postApiDirectoryProperties({ body }), 'property create');
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ['properties'] }),
  });
}
