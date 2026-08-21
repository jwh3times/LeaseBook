import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import {
  download,
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
  unwrap,
  type ApiError,
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
    queryFn: async () =>
      (await unwrap(getApiAccountingBanksBalances(), 'Failed to load bank balances')).rows,
  });
}

export const bankRegisterKey = (bankAccountId: string) => ['bank-register', bankAccountId] as const;

/** The full register for an account (demo scale ≤ 1 page, P42); the page filters/searches client-side. */
export function useBankRegister(bankAccountId: string): UseQueryResult<RegisterResponse> {
  return useQuery({
    queryKey: bankRegisterKey(bankAccountId),
    enabled: bankAccountId !== '',
    queryFn: () =>
      unwrap(
        getApiAccountingBanksByBankAccountIdRegister({
          path: { bankAccountId },
          query: { pageSize: 200 },
        }),
        'Failed to load the register',
      ),
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
    queryFn: async () =>
      (
        await unwrap(
          getApiAccountingReconciliations({ query: { bankAccountId } }),
          'Failed to load reconciliation history',
        )
      ).rows,
  });
}

export function useColumnMappings(bankAccountId: string): UseQueryResult<ColumnMappingView[]> {
  return useQuery({
    queryKey: ['csv-mappings', bankAccountId],
    enabled: bankAccountId !== '',
    queryFn: async () =>
      (
        await unwrap(
          getApiBankingBanksByBankAccountIdMappings({ path: { bankAccountId } }),
          'Failed to load saved mappings',
        )
      ).mappings,
  });
}

// ---- mutations -------------------------------------------------------------

/** A normalized failure from a banking write: the domain `code` (409) or a validation message (400). */
export type BankingError = ApiError;

export async function applyClearances(journalLineIds: string[], cleared = true): Promise<void> {
  await unwrap(
    postApiAccountingBanksClearances({ body: { journalLineIds, cleared } }),
    'Failed to update cleared status',
  );
}

export async function startReconciliation(input: {
  bankAccountId: string;
  year: number;
  month: number;
  statementEndingBalance: number;
}): Promise<ReconciliationView> {
  return unwrap(
    postApiAccountingReconciliations({ body: input }),
    'Failed to start the reconciliation',
  );
}

export async function finalizeReconciliation(id: string): Promise<ReconciliationView> {
  return unwrap(
    postApiAccountingReconciliationsByIdFinalize({ path: { id } }),
    'Failed to finalize the reconciliation',
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
  return unwrap(
    postApiAccountingBanksByBankAccountIdAdjustments({
      path: { bankAccountId },
      body: { bankAccountId, toBankAccountId: input.toBankAccountId ?? null, ...input },
    }),
    'Failed to record the adjustment',
  );
}

export async function importStatement(
  bankAccountId: string,
  input: { filename: string; csvContent: string; columnMap: ColumnMap },
): Promise<ImportResult> {
  return unwrap(
    postApiBankingBanksByBankAccountIdImports({
      path: { bankAccountId },
      body: { bankAccountId, ...input },
    }),
    'Failed to import the statement',
  );
}

export async function fetchMatchPreview(importId: string): Promise<MatchPreviewResponse> {
  return unwrap(
    getApiBankingImportsByImportIdMatches({ path: { importId } }),
    'Failed to load the match preview',
  );
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
  return unwrap(
    postApiBankingImportsByImportIdConfirm({ path: { importId }, body: { importId, decisions } }),
    'Failed to confirm the matches',
  );
}

export async function saveColumnMapping(
  bankAccountId: string,
  input: { name: string; columnMap: ColumnMap },
): Promise<{ id: string }> {
  return unwrap(
    postApiBankingBanksByBankAccountIdMappings({
      path: { bankAccountId },
      body: { bankAccountId, ...input },
    }),
    'Failed to save the column mapping',
  );
}

/**
 * Downloads a finalized reconciliation's stored report as JSON (the immutable snapshot, P64) — an
 * authenticated generated client → blob → anchor, so the cookie rides the request and the file lands
 * named.
 */
export async function downloadReconciliationReport(id: string): Promise<void> {
  // The only download whose body is not already a blob: the route returns the stored snapshot as
  // JSON, so the thunk adapts it before the shared helper sees it.
  await download(
    async () => {
      const { data, error, response } = await getApiAccountingReconciliationsByIdReport({
        path: { id },
      });
      return {
        data: data ? new Blob([data.reportJson ?? '{}'], { type: 'application/json' }) : undefined,
        error,
        response,
      };
    },
    `reconciliation-${id}.json`,
    'Failed to download the reconciliation report',
  );
}
