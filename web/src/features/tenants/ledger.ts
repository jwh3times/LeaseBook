import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import {
  getApiAccountingEntriesByEntryIdAudit,
  getApiAccountingTenantsByTenantIdLedger,
  getApiAccountingTenantsByTenantIdLedgerCsv,
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
    queryFn: async () => {
      const { data, error } = await getApiAccountingTenantsByTenantIdLedger({
        path: { tenantId: id },
      });
      if (error || !data) throw new Error('Failed to load the ledger');
      return data;
    },
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
    queryFn: async () => {
      const { data, error } = await getApiAccountingEntriesByEntryIdAudit({
        path: { entryId },
      });
      if (error || !data) throw new Error('Failed to load the history');
      return data;
    },
  });
}

/**
 * Downloads the focused ledger CSV (P55) through the authenticated generated client, then blob →
 * anchor. The server builds the CSV from the same projection the table renders.
 */
export async function downloadLedgerCsv(tenantId: string): Promise<void> {
  const { data, error } = await getApiAccountingTenantsByTenantIdLedgerCsv({
    path: { tenantId },
    parseAs: 'blob',
  });
  if (error || !(data instanceof Blob)) {
    throw new Error('Failed to export the ledger');
  }

  const blob = data;
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = `tenant-${tenantId}-ledger.csv`;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}
