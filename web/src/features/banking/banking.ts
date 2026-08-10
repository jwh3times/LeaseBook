import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import {
  getApiAccountingBanksBalances,
  getApiAccountingBanksByBankAccountIdRegister,
  getApiAccountingReconciliations,
  getApiAccountingReconciliationsByIdReport,
  getApiBankingBanksByBankAccountIdMappings,
  getApiBankingImportsByImportIdMatches,
  postApiAccountingBanksByBankAccountIdAdjustments,
  postApiAccountingBanksClearances,
  postApiAccountingReconciliations,
  postApiAccountingReconciliationsByIdFinalize,
  postApiBankingBanksByBankAccountIdImports,
  postApiBankingBanksByBankAccountIdMappings,
  postApiBankingImportsByImportIdConfirm,
  primeCsrf,
  type BankBalanceRow,
  type ColumnMap,
  type ColumnMappingView,
  type ConfirmMatchesResult,
  type ImportResult,
  type MatchPreviewResponse,
  type MatchPreviewRow,
  type ReconciliationSummary,
  type ReconciliationView,
  type RegisterResponse,
  type RegisterRow,
  type RegisterTotals,
} from '@/api';
import type { BadgeTone } from '@/design';
import { num } from '@/lib/directory';
import { toApiError, type ApiError } from '@/lib/apiError';

export type {
  BankBalanceRow,
  ColumnMap,
  ColumnMappingView,
  ImportResult,
  MatchPreviewResponse,
  MatchPreviewRow,
  ReconciliationSummary,
  ReconciliationView,
  RegisterResponse,
  RegisterRow,
  RegisterTotals,
};

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
      const { data, error } = await getApiAccountingBanksBalances();
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
      const { data, error } = await getApiAccountingBanksByBankAccountIdRegister({
        path: { bankAccountId },
        query: { pageSize: 200 },
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
      const { data, error } = await getApiAccountingReconciliations({
        query: { bankAccountId },
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
      const { data, error } = await getApiBankingBanksByBankAccountIdMappings({
        path: { bankAccountId },
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
  call: Promise<{ data?: T; error?: unknown; response?: Response }>,
): Promise<T> {
  const { data, error, response } = await call;
  if (data !== undefined && data !== null) return data;
  throw toBankingError(error, response?.status ?? 0);
}

export async function applyClearances(journalLineIds: string[], cleared = true): Promise<void> {
  await primeCsrf();
  await unwrap(
    postApiAccountingBanksClearances({
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
  return unwrap(postApiAccountingReconciliations({ body: input }));
}

export async function finalizeReconciliation(id: string): Promise<ReconciliationView> {
  await primeCsrf();
  return unwrap(
    postApiAccountingReconciliationsByIdFinalize({
      path: { id },
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
    postApiAccountingBanksByBankAccountIdAdjustments({
      path: { bankAccountId },
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
    postApiBankingBanksByBankAccountIdImports({
      path: { bankAccountId },
      body: { bankAccountId, ...input },
    }),
  );
}

export async function fetchMatchPreview(importId: string): Promise<MatchPreviewResponse> {
  const { data, error } = await getApiBankingImportsByImportIdMatches({
    path: { importId },
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
): Promise<ConfirmMatchesResult> {
  await primeCsrf();
  return unwrap(
    postApiBankingImportsByImportIdConfirm({
      path: { importId },
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
    postApiBankingBanksByBankAccountIdMappings({
      path: { bankAccountId },
      body: { bankAccountId, ...input },
    }),
  );
}

/**
 * Downloads a finalized reconciliation's stored report as JSON (the immutable snapshot, P64) — an
 * authenticated generated client → blob → anchor, so the cookie rides the request and the file lands
 * named.
 */
export async function downloadReconciliationReport(id: string): Promise<void> {
  const { data, error } = await getApiAccountingReconciliationsByIdReport({
    path: { id },
  });
  if (error || !data) throw new Error('Failed to download the reconciliation report');
  const blob = new Blob([data.reportJson ?? '{}'], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = `reconciliation-${id}.json`;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}
