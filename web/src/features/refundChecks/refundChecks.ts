import {
  keepPreviousData,
  useQuery,
  type QueryClient,
  type UseQueryResult,
} from '@tanstack/react-query';
import {
  download,
  getApiRefundChecks,
  getApiRefundChecksOptions,
  getApiRefundChecksMicrByBankAccountId,
  getApiRefundChecksPrintSettingsByBankAccountId,
  postApiRefundChecks,
  postApiRefundChecksByIdPdf,
  postApiRefundChecksByIdVoid,
  postApiRefundChecksPrintSettingsByBankAccountIdAlignment,
  putApiRefundChecksMicrByBankAccountId,
  putApiRefundChecksPrintSettingsByBankAccountId,
  unwrap,
  type BankMicrDetailsBody,
  type BankMicrDetailsView,
  type CheckPrintSettingsView,
  type IssueRefundCheckBody,
  type RefundCheckFund,
  type RefundCheckOptions,
  type RefundCheckPage,
  type RefundCheckView,
} from '@/api';
import { num } from '@/lib/directory';

export type {
  BankMicrDetailsBody,
  BankMicrDetailsView,
  CheckPrintSettingsView,
  IssueRefundCheckBody,
  RefundCheckFund,
  RefundCheckPage,
  RefundCheckView,
};

// ---- keys ------------------------------------------------------------------

/** What one tenant's refund check can draw on — every held bucket and its bank's next number. */
export const refundOptionsKey = (tenantId: string) => ['refund-check-options', tenantId] as const;

/**
 * The check list. Both filters are optional server-side; the SPA always passes exactly one. A page and
 * status ride in the same object, so invalidating `{ bankAccountId }` refreshes every page and filter.
 */
export const refundChecksKey = (filter: {
  bankAccountId?: string;
  tenantId?: string;
  status?: RefundCheckFilter;
  page?: number;
}) => ['refund-checks', filter] as const;

/** A bank account's MICR details as the server shows them: last four digits only, never the numbers. */
export const bankMicrKey = (bankAccountId: string) => ['bank-micr', bankAccountId] as const;

export const checkPrintSettingsKey = (bankAccountId: string) =>
  ['check-print-settings', bankAccountId] as const;

// ---- reads -----------------------------------------------------------------

export function useRefundCheckOptions(
  tenantId: string,
  opts: { enabled?: boolean } = {},
): UseQueryResult<RefundCheckOptions> {
  return useQuery({
    queryKey: refundOptionsKey(tenantId),
    enabled: (opts.enabled ?? true) && tenantId !== '',
    queryFn: () =>
      unwrap(
        getApiRefundChecksOptions({ query: { tenantId } }),
        'Failed to load the funds this tenant can be refunded from.',
      ),
  });
}

/** `all` lists every check; `outstanding` asks the server for the ones not yet cleared or voided (#476). */
export type RefundCheckFilter = 'all' | 'outstanding';

export const REFUND_CHECK_PAGE_SIZE = 50;

export function useRefundChecks(
  bankAccountId: string,
  { status, page }: { status: RefundCheckFilter; page: number },
): UseQueryResult<RefundCheckPage> {
  return useQuery({
    queryKey: refundChecksKey({ bankAccountId, status, page }),
    enabled: bankAccountId !== '',
    // Paging and filtering swap the key; keeping the last rows mounted keeps the pager (and focus).
    placeholderData: keepPreviousData,
    queryFn: () =>
      unwrap(
        getApiRefundChecks({
          query: {
            bankAccountId,
            status: status === 'outstanding' ? 'outstanding' : undefined,
            page,
            pageSize: REFUND_CHECK_PAGE_SIZE,
          },
        }),
        'Failed to load the refund checks for this account.',
      ),
  });
}

export function useCheckPrintSettings(
  bankAccountId: string,
): UseQueryResult<CheckPrintSettingsView> {
  return useQuery({
    queryKey: checkPrintSettingsKey(bankAccountId),
    enabled: bankAccountId !== '',
    queryFn: () =>
      unwrap(
        getApiRefundChecksPrintSettingsByBankAccountId({ path: { bankAccountId } }),
        'Failed to load the print settings for this account.',
      ),
  });
}

// ---- writes ----------------------------------------------------------------

export function useBankMicrDetails(bankAccountId: string): UseQueryResult<BankMicrDetailsView> {
  return useQuery({
    queryKey: bankMicrKey(bankAccountId),
    enabled: bankAccountId !== '',
    queryFn: () =>
      unwrap(
        getApiRefundChecksMicrByBankAccountId({ path: { bankAccountId } }),
        'Failed to load the MICR details for this account.',
      ),
  });
}

/**
 * Saves a bank account's MICR details (#474). Called directly rather than through `useMutation`, whose
 * cache would keep the full routing and account numbers as the mutation's variables; the response the
 * caller caches carries only their last four digits.
 */
export async function saveBankMicrDetails(
  bankAccountId: string,
  body: BankMicrDetailsBody,
): Promise<BankMicrDetailsView> {
  return unwrap(
    putApiRefundChecksMicrByBankAccountId({ path: { bankAccountId }, body }),
    'Failed to save the MICR details.',
  );
}

export async function issueRefundCheck(body: IssueRefundCheckBody): Promise<RefundCheckView> {
  return unwrap(postApiRefundChecks({ body }), 'Failed to issue the refund check.');
}

export async function voidRefundCheck(id: string, reason: string): Promise<RefundCheckView> {
  return unwrap(
    postApiRefundChecksByIdVoid({ path: { id }, body: { reason } }),
    'Failed to void the refund check.',
  );
}

export async function saveCheckPrintSettings(
  bankAccountId: string,
  offsetXPoints: number,
  offsetYPoints: number,
): Promise<CheckPrintSettingsView> {
  return unwrap(
    putApiRefundChecksPrintSettingsByBankAccountId({
      path: { bankAccountId },
      body: { offsetXPoints, offsetYPoints },
    }),
    'Failed to save the print settings.',
  );
}

/**
 * Renders a check as a PDF and hands it to the browser as a download. Each call records a print on
 * the server, which is why the route is a POST and why the bytes are never cached here: the response
 * is `no-store`, carries a payee and an amount, and goes straight from the request to the anchor
 * inside {@link download}, which revokes its object URL immediately.
 */
export async function printRefundCheck(check: { id: string; checkNumber: number | string }) {
  await download(
    () => postApiRefundChecksByIdPdf({ path: { id: check.id }, parseAs: 'blob' }),
    `refund-check-${num(check.checkNumber)}.pdf`,
    'Failed to print the check.',
  );
}

export async function printAlignmentPage(bankAccountId: string) {
  await download(
    () =>
      postApiRefundChecksPrintSettingsByBankAccountIdAlignment({
        path: { bankAccountId },
        parseAs: 'blob',
      }),
    'check-alignment-test.pdf',
    'Failed to print the alignment page.',
  );
}

/**
 * Everything a refund-check issue or void changes: the check list, the tenant's ledger and header
 * (balance, deposit held), the refundable funds, the bank's register and balances, and the held-deposit
 * figures on the dashboard, owner pages, statements and reports. Prefix keys, so every filter of a list is refreshed.
 */
export function invalidateAfterRefundCheck(
  queryClient: QueryClient,
  scope: { tenantId?: string; bankAccountId?: string },
): void {
  void queryClient.invalidateQueries({ queryKey: ['refund-checks'] });
  void queryClient.invalidateQueries({ queryKey: ['refund-check-options'] });
  if (scope.tenantId) {
    void queryClient.invalidateQueries({ queryKey: ['tenant-ledger', scope.tenantId] });
    void queryClient.invalidateQueries({ queryKey: ['tenant', scope.tenantId] });
  }
  if (scope.bankAccountId) {
    void queryClient.invalidateQueries({ queryKey: ['bank-register', scope.bankAccountId] });
  }
  void queryClient.invalidateQueries({ queryKey: ['bank-balances'] });
  void queryClient.invalidateQueries({ queryKey: ['statement'] });
  void queryClient.invalidateQueries({ queryKey: ['report-preview'] });
  void queryClient.invalidateQueries({ queryKey: ['tenants'] });
  void queryClient.invalidateQueries({ queryKey: ['owners'] });
  void queryClient.invalidateQueries({ queryKey: ['owner'] });
  void queryClient.invalidateQueries({ queryKey: ['dashboard'] });
}

/**
 * The refund-check budget (#473, maintainer decision 2026-10-01): open Refund… → confirm the
 * prefilled fields → Issue → Print, at most four interactions. Typing the address is data entry and is
 * not counted, matching the payment budget.
 */
export const REFUND_CHECK_BUDGET = 4;

// ---- labels ----------------------------------------------------------------

export function sourceLabel(source: string): string {
  return source === 'deposit'
    ? 'Security deposit'
    : source === 'prepayment'
      ? 'Prepaid credit'
      : source;
}

// ---- money and field parsing -----------------------------------------------

/**
 * Parses a typed amount into whole cents without floating-point arithmetic: digits, then at most two
 * decimals. `null` for anything else, including a value with more decimals than money carries.
 */
export function parseAmountCents(text: string): number | null {
  const match = /^(\d{1,7})(?:\.(\d{0,2}))?$/.exec(text.trim());
  if (!match) return null;
  const whole = Number(match[1]);
  const fraction = Number((match[2] ?? '').padEnd(2, '0'));
  return whole * 100 + fraction;
}

/** A server decimal (number or numeric string) as whole cents, for comparison only. */
export function toCents(value: number | string): number {
  return Math.round(num(value) * 100);
}

/** Whole cents → the decimal the API takes. Exact: a two-decimal value round-trips through JSON. */
export function centsToAmount(cents: number): number {
  return Number(`${Math.trunc(cents / 100)}.${String(cents % 100).padStart(2, '0')}`);
}

/** A server decimal as the text an amount input holds (`1450.00`), never a display string. */
export function amountInputValue(value: number | string): string {
  const cents = toCents(value);
  return `${Math.trunc(cents / 100)}.${String(cents % 100).padStart(2, '0')}`;
}

/** Local calendar date as `yyyy-MM-dd` — the date the operator is writing the check, not UTC's. */
export function todayLocalIso(now = new Date()): string {
  const month = String(now.getMonth() + 1).padStart(2, '0');
  const day = String(now.getDate()).padStart(2, '0');
  return `${now.getFullYear()}-${month}-${day}`;
}

export const STATE_PATTERN = /^[A-Z]{2}$/;
export const POSTAL_PATTERN = /^\d{5}(-\d{4})?$/;
export const MAX_CHECK_NUMBER = 99_999_999;

/** A client-side idempotency key: minted once per dialog open so a retry cannot double-issue. */
export function newIssueKey(): string {
  if (globalThis.crypto?.randomUUID) return globalThis.crypto.randomUUID();
  // The server binds the key to a Guid, so the fallback still has to be a UUID (v4 layout).
  const bytes = new Uint8Array(16);
  globalThis.crypto.getRandomValues(bytes);
  bytes[6] = ((bytes[6] ?? 0) & 0x0f) | 0x40;
  bytes[8] = ((bytes[8] ?? 0) & 0x3f) | 0x80;
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/** Print offsets are bounded to one inch either way, in hundredths of a point at most. */
export const OFFSET_LIMIT_POINTS = 72;

export function parseOffset(text: string, limit = OFFSET_LIMIT_POINTS): number | null {
  const trimmed = text.trim();
  if (!/^[-−]?\d{1,2}(\.\d{1,2})?$/.test(trimmed)) return null;
  const value = Number(trimmed.replace('−', '-'));
  return Math.abs(value) <= limit ? value : null;
}

/** The MICR line moves at most a quarter inch from its nominal position, in hundredths of a point. */
export const MICR_OFFSET_LIMIT_POINTS = 18;
