import { api, primeCsrf, type components } from '@/api';

export type PostResult = components['schemas']['PostResult'];

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
export async function submitLedgerEntry(tenantId: string, input: LedgerEntryInput): Promise<PostResult> {
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
          body: { tenantId, amount, date, depositBankId: bankAccountId, memo: memoOrNull, sourceRef },
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
          body: { tenantId, amount, date, kind: CHARGE_KIND[category] ?? 'other', memo: memoOrNull, sourceRef },
        }),
      );
  }
}
