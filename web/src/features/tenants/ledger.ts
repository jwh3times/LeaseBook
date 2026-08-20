import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import {
  download,
  getApiAccountingEntriesByEntryIdAudit,
  getApiAccountingTenantsByTenantIdLedger,
  getApiAccountingTenantsByTenantIdLedgerCsv,
  unwrap,
  type AuditRow,
  type EntryAuditResponse,
  type TenantLedgerEntry,
  type TenantLedgerResponse,
} from '@/api';

export type { AuditRow, EntryAuditResponse, TenantLedgerEntry, TenantLedgerResponse };

/** Query key for a tenant's ledger — WP-05/06 mutations invalidate this to refetch + flash the new row. */
export const tenantLedgerKey = (id: string) => ['tenant-ledger', id] as const;

export function useTenantLedger(id: string): UseQueryResult<TenantLedgerResponse> {
  return useQuery({
    queryKey: tenantLedgerKey(id),
    queryFn: () =>
      unwrap(
        getApiAccountingTenantsByTenantIdLedger({ path: { tenantId: id } }),
        'Failed to load the ledger',
      ),
  });
}

/** The per-entry audit trail (P56): who/when/what for an entry and its reversal, fetched when opened. */
export function useEntryAudit(
  entryId: string,
  enabled: boolean,
): UseQueryResult<EntryAuditResponse> {
  return useQuery({
    queryKey: ['entry-audit', entryId],
    enabled,
    queryFn: () =>
      unwrap(
        getApiAccountingEntriesByEntryIdAudit({ path: { entryId } }),
        'Failed to load the history',
      ),
  });
}

/**
 * Downloads the focused ledger CSV (P55) through the authenticated generated client, then blob →
 * anchor. The server builds the CSV from the same projection the table renders.
 */
export async function downloadLedgerCsv(tenantId: string): Promise<void> {
  await download(
    () => getApiAccountingTenantsByTenantIdLedgerCsv({ path: { tenantId }, parseAs: 'blob' }),
    `tenant-${tenantId}-ledger.csv`,
    'Failed to export the ledger',
  );
}
