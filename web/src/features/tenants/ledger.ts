import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { api, type components } from '@/api';

export type TenantLedgerResponse = components['schemas']['TenantLedgerResponse'];
export type TenantLedgerEntry = components['schemas']['TenantLedgerEntry'];

/** Query key for a tenant's ledger — WP-05/06 mutations invalidate this to refetch + flash the new row. */
export const tenantLedgerKey = (id: string) => ['tenant-ledger', id] as const;

export function useTenantLedger(id: string): UseQueryResult<TenantLedgerResponse> {
  return useQuery({
    queryKey: tenantLedgerKey(id),
    queryFn: async () => {
      const { data, error } = await api.GET('/api/accounting/tenants/{tenantId}/ledger', {
        params: { path: { tenantId: id } },
      });
      if (error || !data) throw new Error('Failed to load the ledger');
      return data;
    },
  });
}

/**
 * Downloads the focused ledger CSV (P55) through an authenticated fetch → blob → anchor, so the cookie
 * rides the request and the file lands with a sensible name. The server builds the CSV from the same
 * projection the table renders.
 */
export async function downloadLedgerCsv(tenantId: string): Promise<void> {
  const response = await fetch(`/api/accounting/tenants/${tenantId}/ledger.csv`, { credentials: 'include' });
  if (!response.ok) {
    throw new Error('Failed to export the ledger');
  }

  const blob = await response.blob();
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = `tenant-${tenantId}-ledger.csv`;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}
