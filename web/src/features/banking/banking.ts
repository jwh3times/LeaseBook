import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import {
  download,
  getApiAccountingBanksBalances,
  getApiAccountingBanksByBankAccountIdHeldFees,
  getApiAccountingBanksByBankAccountIdRegister,
  getApiAccountingReconciliations,
  getApiAccountingReconciliationsByIdLines,
  getApiAccountingReconciliationsByIdReport,
  getApiBankingBanksByBankAccountIdMappings,
  getApiBankingImportsByImportIdMatches,
  postApiAccountingBanksByBankAccountIdAdjustments,
  postApiAccountingBanksClearances,
  postApiAccountingReconciliations,
  postApiAccountingReconciliationsByIdFinalize,
  postApiAccountingReconciliationsByIdUnlock,
  postApiBankingBanksByBankAccountIdImports,
  postApiBankingBanksByBankAccountIdMappings,
  postApiBankingImportsByImportIdConfirm,
  postApiBankingImportsHeaders,
  unwrap,
  type ApiError,
  type BankBalanceRow,
  type ColumnMap,
  type ColumnMappingView,
  type ConfirmMatchesResult,
  type HeldFeesView,
  type ImportResult,
  type MatchPreviewResponse,
  type MatchPreviewRow,
  type ReconciliationLine,
  type ReconciliationLinesResponse,
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
  HeldFeesView,
  ImportResult,
  MatchPreviewResponse,
  MatchPreviewRow,
  ReconciliationLine,
  ReconciliationLinesResponse,
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

export const heldFeesKey = (bankAccountId: string) => ['held-fees', bankAccountId] as const;

/**
 * The management fees held in one bank today, and the lowest they are on or after `from` (#743): the
 * most a sweep, a bank fee or a transfer dated `from` may take, which the server's guard reads too.
 */
export function useHeldFees(
  bankAccountId: string,
  from: string,
  opts: { enabled?: boolean } = {},
): UseQueryResult<HeldFeesView> {
  return useQuery({
    queryKey: [...heldFeesKey(bankAccountId), from],
    enabled: (opts.enabled ?? true) && bankAccountId !== '' && /^\d{4}-\d{2}-\d{2}$/.test(from),
    queryFn: () =>
      unwrap(
        getApiAccountingBanksByBankAccountIdHeldFees({
          path: { bankAccountId },
          query: { from },
        }),
        'Failed to load the fees held in this account',
      ),
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

export const reconciliationLinesKey = (reconciliationId: string) =>
  ['reconciliation-lines', reconciliationId] as const;

/**
 * What a month's reconciliation may be finalized against (#656): the balance already reconciled through
 * it, and every unreconciled bank line dated through its last day, cleared or not. Finalize again ticks
 * lines from this list, so the difference the view shows is the one finalize computes.
 */
export function useReconciliationLines(
  reconciliationId: string,
): UseQueryResult<ReconciliationLinesResponse> {
  return useQuery({
    queryKey: reconciliationLinesKey(reconciliationId),
    // Every write on the account can change which lines are unreconciled, so the view asks again
    // each time it opens rather than trusting a list read before a correction was posted.
    refetchOnMount: 'always',
    queryFn: () =>
      unwrap(
        getApiAccountingReconciliationsByIdLines({ path: { id: reconciliationId } }),
        'Failed to load the lines this month can be reconciled with',
      ),
  });
}

/** The saved CSV column mappings of one bank account; a save invalidates it so the wizard offers it. */
export const columnMappingsKey = (bankAccountId: string) =>
  ['csv-mappings', bankAccountId] as const;

export function useColumnMappings(bankAccountId: string): UseQueryResult<ColumnMappingView[]> {
  return useQuery({
    queryKey: columnMappingsKey(bankAccountId),
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

/**
 * Finalizes a reconciliation. With `assignedLineIds` the month is finalized against exactly those lines
 * (an uncleared one is cleared, a payout widens to its whole payout); without, the server assigns the
 * current month's cleared lines and refuses a past month (`reconciliation_lines_required`, #656).
 */
export async function finalizeReconciliation(
  id: string,
  assignedLineIds?: string[],
): Promise<ReconciliationView> {
  return unwrap(
    postApiAccountingReconciliationsByIdFinalize({
      path: { id },
      ...(assignedLineIds ? { body: { assignedLineIds } } : {}),
    }),
    'Failed to finalize the reconciliation',
  );
}

/**
 * Reopens a finalized month (PMAdmin + reason, ADR-014 §4): postings dated into it land on the account
 * again until it is finalized again. The route's policy answers a non-admin with a bare 403.
 */
export async function unlockReconciliation(
  id: string,
  reason: string,
): Promise<ReconciliationView> {
  return unwrap(
    postApiAccountingReconciliationsByIdUnlock({
      path: { id },
      body: { reconciliationId: id, reason },
    }),
    'Failed to unlock the reconciliation',
  );
}

/**
 * The adjustments the register offers: a bank fee, a transfer between trust accounts (#553), a PM
 * contribution into a trust account (#720), a sweep of the PM's held fees to the PM operating
 * account (#743), and an owner's contribution into an operating trust account (#744). The server also
 * takes `interest`, which no screen offers until #307 settles who interest belongs to.
 */
export type BankAdjustmentKind =
  'fee' | 'transfer' | 'contribution' | 'sweep' | 'owner-contribution';

/** Records a bank adjustment the register offers; the one caller path for the route. */
export async function recordBankAdjustment(
  bankAccountId: string,
  input: {
    kind: BankAdjustmentKind;
    amount: number;
    date: string;
    description: string | null;
    toBankAccountId?: string | null;
    sourceRef: string;
    /** Staff-only (#468); never on a statement. */
    internalNote?: string | null;
    /** An owner contribution's owner and property (#744); null for every other kind. */
    ownerId?: string | null;
    propertyId?: string | null;
  },
): Promise<{ entryId: string }> {
  return unwrap(
    postApiAccountingBanksByBankAccountIdAdjustments({
      path: { bankAccountId },
      body: {
        bankAccountId,
        kind: input.kind,
        amount: input.amount,
        date: input.date,
        description: input.description,
        toBankAccountId: input.toBankAccountId ?? null,
        sourceRef: input.sourceRef,
        internalNote: input.internalNote ?? null,
        ownerId: input.ownerId ?? null,
        propertyId: input.propertyId ?? null,
      },
    }),
    'Failed to record the adjustment',
  );
}

/** The column names the server reads from this file, which are the ones a column map can name (#651). */
export async function readStatementHeaders(csvContent: string): Promise<string[]> {
  return (
    await unwrap(
      postApiBankingImportsHeaders({ body: { csvContent } }),
      'Failed to read the columns of this file',
    )
  ).headers;
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
  /** Set instead of `journalLineId` when the line matched a whole payout; its bank lines clear together. */
  groupRef?: string;
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
