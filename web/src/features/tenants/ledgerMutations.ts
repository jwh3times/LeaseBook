import { api, primeCsrf, type components } from '@/api';

export type PostResult = components['schemas']['PostResult'];

/** A client-side idempotency key (P54): minted once per composer/modal open. */
export function newSourceRef(): string {
  if (globalThis.crypto?.randomUUID) return globalThis.crypto.randomUUID();
  // Fallback for contexts without randomUUID — getRandomValues is more broadly supported.
  const bytes = new Uint8Array(16);
  globalThis.crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}

/** A normalized failure from a ledger post: the domain `code` (422/409), or a validation message (400). */
export interface LedgerPostError {
  code?: string;
  existingEntryId?: string;
  message: string;
}

interface ProblemBody {
  code?: string;
  existingEntryId?: string;
  detail?: string;
  title?: string;
  errors?: Record<string, string[]>;
}

function toError(error: unknown, status: number): LedgerPostError {
  const body = (error ?? {}) as ProblemBody;
  const firstValidation = body.errors ? Object.values(body.errors)[0]?.[0] : undefined;
  return {
    code: body.code,
    existingEntryId: body.existingEntryId,
    message: firstValidation ?? body.detail ?? body.title ?? `Request failed (${status}).`,
  };
}

async function unwrap(
  call: Promise<{ data?: PostResult; error?: unknown; response: Response }>,
): Promise<PostResult> {
  const { data, error, response } = await call;
  if (data) return data;
  throw toError(error, response.status);
}

/** The composer/apply fields, pre-coerced. `category` drives which command (and event) is posted. */
export interface LedgerEntryInput {
  category: string; // 'Payment' | 'Rent' | 'Late Fee' | 'Maintenance' | 'Other' | 'Security Deposit' | 'Prepayment' | 'Credit'
  amount: number;
  date: string; // yyyy-mm-dd
  memo: string;
  method: string; // ach | card | check | cash (payment)
  bankAccountId: string;
  sourceRef: string;
}

const CHARGE_KIND: Record<string, string> = {
  Rent: 'rent',
  'Late Fee': 'late',
  Maintenance: 'maintenance-recharge',
  Other: 'other',
};

/** Categories the composer can post (charge kinds plus the bank-backed and credit variants). */
export const COMPOSER_CHARGE_CATEGORIES = [
  'Rent',
  'Late Fee',
  'Maintenance',
  'Other',
  'Security Deposit',
  'Prepayment',
  'Credit',
] as const;

export const PAYMENT_METHODS: { value: string; label: string }[] = [
  { value: 'ach', label: 'ACH' },
  { value: 'card', label: 'Card' },
  { value: 'check', label: 'Check' },
  { value: 'cash', label: 'Cash' },
];

/** Whether a category posts into a bank (so the composer shows + defaults the bank picker). */
export function categoryNeedsBank(category: string): boolean {
  return category === 'Payment' || category === 'Security Deposit' || category === 'Prepayment';
}

/** The bank purpose a category defaults to: deposits → the deposit trust, everything else → operating trust. */
export function bankPurposeFor(category: string): 'trust' | 'deposit' {
  return category === 'Security Deposit' ? 'deposit' : 'trust';
}

/** Posts the right WP-01 command for the chosen category. Throws a {@link LedgerPostError} on rejection. */
export async function submitLedgerEntry(
  tenantId: string,
  input: LedgerEntryInput,
): Promise<PostResult> {
  await primeCsrf();
  const { category, amount, date, memo, method, bankAccountId, sourceRef } = input;
  const trimmed = memo.trim();
  const memoOrNull = trimmed === '' ? null : trimmed;
  const path = { path: { tenantId } } as const;

  switch (category) {
    case 'Payment':
      return unwrap(
        api.POST('/api/accounting/tenants/{tenantId}/payments', {
          params: path,
          body: { tenantId, amount, date, method, bankAccountId, memo: memoOrNull, sourceRef },
        }),
      );
    case 'Security Deposit':
      return unwrap(
        api.POST('/api/accounting/tenants/{tenantId}/deposits', {
          params: path,
          body: {
            tenantId,
            amount,
            date,
            depositBankId: bankAccountId,
            memo: memoOrNull,
            sourceRef,
          },
        }),
      );
    case 'Prepayment':
      return unwrap(
        api.POST('/api/accounting/tenants/{tenantId}/prepayments', {
          params: path,
          body: { tenantId, amount, date, bankAccountId, memo: memoOrNull, sourceRef },
        }),
      );
    case 'Credit':
      return unwrap(
        api.POST('/api/accounting/tenants/{tenantId}/credits', {
          params: path,
          body: { tenantId, amount, date, reason: memoOrNull ?? 'Credit', sourceRef },
        }),
      );
    default:
      return unwrap(
        api.POST('/api/accounting/tenants/{tenantId}/charges', {
          params: path,
          body: {
            tenantId,
            amount,
            date,
            kind: CHARGE_KIND[category] ?? 'other',
            memo: memoOrNull,
            sourceRef,
          },
        }),
      );
  }
}

/** Voids a posted entry → a linked reversal (P54 idempotency key; default as-of today server-side). */
export async function voidEntry(
  entryId: string,
  reason: string,
  sourceRef: string,
): Promise<PostResult> {
  await primeCsrf();
  return unwrap(
    api.POST('/api/accounting/entries/{entryId}/void', {
      params: { path: { entryId } },
      body: { entryId, reason, asOfDate: null, sourceRef },
    }),
  );
}

export interface ApplyDepositInput {
  amount: number;
  date: string;
  depositBankId: string;
  operatingBankId: string;
  target: string; // to-owner-income | against-charges
  reason: string;
  sourceRef: string;
}

export async function applyDeposit(
  tenantId: string,
  input: ApplyDepositInput,
): Promise<PostResult> {
  await primeCsrf();
  return unwrap(
    api.POST('/api/accounting/tenants/{tenantId}/deposit-applications', {
      params: { path: { tenantId } },
      body: {
        tenantId,
        amount: input.amount,
        date: input.date,
        depositBankId: input.depositBankId,
        operatingBankId: input.operatingBankId,
        target: input.target,
        reason: input.reason,
        sourceRef: input.sourceRef,
      },
    }),
  );
}

export interface ApplyPrepaymentInput {
  amount: number;
  date: string;
  bankAccountId: string;
  memo: string;
  sourceRef: string;
}

export async function applyPrepayment(
  tenantId: string,
  input: ApplyPrepaymentInput,
): Promise<PostResult> {
  await primeCsrf();
  const memo = input.memo.trim();
  return unwrap(
    api.POST('/api/accounting/tenants/{tenantId}/prepayment-applications', {
      params: { path: { tenantId } },
      body: {
        tenantId,
        amount: input.amount,
        date: input.date,
        bankAccountId: input.bankAccountId,
        memo: memo === '' ? null : memo,
        sourceRef: input.sourceRef,
      },
    }),
  );
}
