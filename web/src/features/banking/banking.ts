import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { api, primeCsrf, type components } from '@/api';
import type { BadgeTone } from '@/design';
import { num } from '@/lib/directory';
import { toApiError, type ApiError } from '@/lib/apiError';

export type BankBalanceRow = components['schemas']['BankBalanceRow'];
export type RegisterResponse = components['schemas']['RegisterResponse'];
export type RegisterRow = components['schemas']['RegisterRow'];
export type RegisterTotals = components['schemas']['RegisterTotals'];
export type ReconciliationView = components['schemas']['ReconciliationView'];
export type ReconciliationSummary = components['schemas']['ReconciliationSummary'];
export type ImportResult = components['schemas']['ImportResult'];
export type MatchPreviewResponse = components['schemas']['MatchPreviewResponse'];
export type MatchPreviewRow = components['schemas']['MatchPreviewRow'];
export type ColumnMap = components['schemas']['ColumnMap'];
export type ColumnMappingView = components['schemas']['ColumnMappingView'];

/**
 * The clearance status enum crosses the wire as a number (0/1/2 — no string-enum converter on the host),
 * matching the C# `BankLineStatus { Uncleared, Cleared, Reconciled }` order. Status is never color-alone
 * (CLAUDE.md): the label carries it; tone + dot are decoration.
 */
export const STATUS_META: Record<number, { key: string; label: string; tone: BadgeTone }> = {
  0: { key: 'uncleared', label: 'Uncleared', tone: 'warn' },
  1: { key: 'cleared', label: 'Cleared', tone: 'accent' },
  2: { key: 'reconciled', label: 'Reconciled', tone: 'pos' },
};

export const STATUS = { uncleared: 0, cleared: 1, reconciled: 2 } as const;

/** Always-defined status metadata (an unknown code falls back to uncleared). */
export function statusMeta(status: number): { key: string; label: string; tone: BadgeTone } {
  return STATUS_META[status] ?? { key: 'uncleared', label: 'Uncleared', tone: 'warn' };
}

/** Signed register amount: a deposit is +, a withdrawal is − (mirrors the matcher's convention). */
export function rowAmount(row: RegisterRow): number {
  return row.deposit != null ? num(row.deposit) : -num(row.withdrawal);
}

// ---- queries ---------------------------------------------------------------

export function useBankBalances(
  opts: { enabled?: boolean } = {},
): UseQueryResult<BankBalanceRow[]> {
  return useQuery({
    queryKey: ['bank-balances'],
    enabled: opts.enabled ?? true,
    queryFn: async () => {
      const { data, error } = await api.GET('/api/accounting/banks/balances');
      if (error || !data) throw new Error('Failed to load bank balances');
      return data.rows;
    },
  });
}

export const bankRegisterKey = (bankAccountId: string) => ['bank-register', bankAccountId] as const;

/** The full register for an account (demo scale ≤ 1 page, P42); the page filters/searches client-side. */
export function useBankRegister(bankAccountId: string): UseQueryResult<RegisterResponse> {
  return useQuery({
    queryKey: bankRegisterKey(bankAccountId),
    enabled: bankAccountId !== '',
    queryFn: async () => {
      const { data, error } = await api.GET('/api/accounting/banks/{bankAccountId}/register', {
        params: { path: { bankAccountId }, query: { pageSize: 200 } },
      });
      if (error || !data) throw new Error('Failed to load the register');
      return data;
    },
  });
}

export const reconciliationHistoryKey = (bankAccountId: string) =>
  ['reconciliations', bankAccountId] as const;

export function useReconciliationHistory(
  bankAccountId: string,
): UseQueryResult<ReconciliationSummary[]> {
  return useQuery({
    queryKey: reconciliationHistoryKey(bankAccountId),
    enabled: bankAccountId !== '',
    queryFn: async () => {
      const { data, error } = await api.GET('/api/accounting/reconciliations', {
        params: { query: { bankAccountId } },
      });
      if (error || !data) throw new Error('Failed to load reconciliation history');
      return data.rows;
    },
  });
}

export function useColumnMappings(bankAccountId: string): UseQueryResult<ColumnMappingView[]> {
  return useQuery({
    queryKey: ['csv-mappings', bankAccountId],
    enabled: bankAccountId !== '',
    queryFn: async () => {
      const { data, error } = await api.GET('/api/banking/banks/{bankAccountId}/mappings', {
        params: { path: { bankAccountId } },
      });
      if (error || !data) throw new Error('Failed to load saved mappings');
      return data.mappings;
    },
  });
}

// ---- mutations -------------------------------------------------------------

/** A normalized failure from a banking write: the domain `code` (409) or a validation message (400). */
export type BankingError = ApiError;
const toBankingError = toApiError;

async function unwrap<T>(
  call: Promise<{ data?: T; error?: unknown; response: Response }>,
): Promise<T> {
  const { data, error, response } = await call;
  if (data !== undefined && data !== null) return data;
  throw toBankingError(error, response.status);
}

export async function applyClearances(journalLineIds: string[], cleared = true): Promise<void> {
  await primeCsrf();
  await unwrap(
    api.POST('/api/accounting/banks/clearances', {
      body: { journalLineIds, cleared },
    }),
  );
}

export async function startReconciliation(input: {
  bankAccountId: string;
  year: number;
  month: number;
  statementEndingBalance: number;
}): Promise<ReconciliationView> {
  await primeCsrf();
  return unwrap(api.POST('/api/accounting/reconciliations', { body: input }));
}

export async function finalizeReconciliation(id: string): Promise<ReconciliationView> {
  await primeCsrf();
  return unwrap(
    api.POST('/api/accounting/reconciliations/{id}/finalize', {
      params: { path: { id } },
    }),
  );
}

export async function recordBankAdjustment(
  bankAccountId: string,
  input: {
    kind: string;
    amount: number;
    date: string;
    memo: string | null;
    toBankAccountId?: string | null;
    sourceRef: string;
  },
): Promise<{ entryId: string }> {
  await primeCsrf();
  return unwrap(
    api.POST('/api/accounting/banks/{bankAccountId}/adjustments', {
      params: { path: { bankAccountId } },
      body: { bankAccountId, toBankAccountId: input.toBankAccountId ?? null, ...input },
    }),
  );
}

export async function importStatement(
  bankAccountId: string,
  input: { filename: string; csvContent: string; columnMap: ColumnMap },
): Promise<ImportResult> {
  await primeCsrf();
  return unwrap(
    api.POST('/api/banking/banks/{bankAccountId}/imports', {
      params: { path: { bankAccountId } },
      body: { bankAccountId, ...input },
    }),
  );
}

export async function fetchMatchPreview(importId: string): Promise<MatchPreviewResponse> {
  const { data, error } = await api.GET('/api/banking/imports/{importId}/matches', {
    params: { path: { importId } },
  });
  if (error || !data) throw new Error('Failed to load the match preview');
  return data;
}

export interface ConfirmDecision {
  statementLineId: string;
  journalLineId: string | null;
  kind: string;
}

export async function confirmMatches(
  importId: string,
  decisions: ConfirmDecision[],
): Promise<components['schemas']['ConfirmMatchesResult']> {
  await primeCsrf();
  return unwrap(
    api.POST('/api/banking/imports/{importId}/confirm', {
      params: { path: { importId } },
      body: { importId, decisions },
    }),
  );
}

export async function saveColumnMapping(
  bankAccountId: string,
  input: { name: string; columnMap: ColumnMap },
): Promise<{ id: string }> {
  await primeCsrf();
  return unwrap(
    api.POST('/api/banking/banks/{bankAccountId}/mappings', {
      params: { path: { bankAccountId } },
      body: { bankAccountId, ...input },
    }),
  );
}

/**
 * Downloads a finalized reconciliation's stored report as JSON (the immutable snapshot, P64) — an
 * authenticated fetch → blob → anchor, so the cookie rides the request and the file lands named.
 */
export async function downloadReconciliationReport(id: string): Promise<void> {
  const response = await fetch(`/api/accounting/reconciliations/${id}/report`, {
    credentials: 'include',
  });
  if (!response.ok) throw new Error('Failed to download the reconciliation report');
  const report = (await response.json()) as components['schemas']['ReconciliationReportResponse'];
  const blob = new Blob([report.reportJson ?? '{}'], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = `reconciliation-${id}.json`;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}
