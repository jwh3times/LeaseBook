import {
  postApiAccountingEntriesByEntryIdVoid,
  postApiAccountingTenantsByTenantIdCharges,
  postApiAccountingTenantsByTenantIdCredits,
  postApiAccountingTenantsByTenantIdDepositApplications,
  postApiAccountingTenantsByTenantIdDeposits,
  postApiAccountingTenantsByTenantIdPayments,
  postApiAccountingTenantsByTenantIdPrepaymentApplications,
  postApiAccountingTenantsByTenantIdPrepayments,
  type PostResult,
} from '@/api';
import { toApiError, type ApiError } from '@/api';

export type { PostResult };

/** A client-side idempotency key (P54): minted once per composer/modal open. */
export function newSourceRef(): string {
  if (globalThis.crypto?.randomUUID) return globalThis.crypto.randomUUID();
  // Fallback for contexts without randomUUID — getRandomValues is more broadly supported.
  const bytes = new Uint8Array(16);
  globalThis.crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}

/**
 * Friendly copy for the M4 per-account reconciliation lock (`account_period_locked`, 409): a bank line
 * dated into a finalized account-month is rejected. Surfaced inline in the composer/apply/void the same
 * way `insufficient_receivable` is, so the user moves the date into the open month.
 */
export const LOCKED_PERIOD_MESSAGE =
  "This account's month is reconciled and locked — post into the open month, or unlock the reconciliation first.";

/** A normalized failure from a ledger post: the domain `code` (422/409), or a validation message (400). */
export interface LedgerPostError extends ApiError {
  existingEntryId?: string;
}

function toError(error: unknown, status: number): LedgerPostError {
  const body = (error ?? {}) as { existingEntryId?: string };
  // Do not break the P54 duplicate-as-success path: LedgerComposer.tsx:87-91 reads this.
  return { ...toApiError(error, status), existingEntryId: body.existingEntryId };
}

async function unwrap(
  call: Promise<{ data?: PostResult; error?: unknown; response?: Response }>,
): Promise<PostResult> {
  const { data, error, response } = await call;
  if (data) return data;
  throw toError(error, response?.status ?? 0);
}

/** A trimmed free-text field, or null when blank — the server's "not supplied". */
function textOrNull(value: string | undefined): string | null {
  const trimmed = (value ?? '').trim();
  return trimmed === '' ? null : trimmed;
}

/** The composer/apply fields, pre-coerced. `category` drives which command (and event) is posted. */
export interface LedgerEntryInput {
  category: string; // 'Payment' | 'Rent' | 'Late Fee' | 'Maintenance' | 'Other' | 'Security Deposit' | 'Prepayment' | 'Credit'
  amount: number;
  date: string; // yyyy-mm-dd
  /**
   * Owner-facing (#468, ADR-047): prints on the owner statement. For a credit this is the reason the
   * `IssueCredit` command carries, which is owner-facing too.
   */
  description: string;
  /** Staff-only: shown on staff surfaces, never on an owner's or resident's copy. */
  internalNote?: string;
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
  const { category, amount, date, method, bankAccountId, sourceRef } = input;
  const description = textOrNull(input.description);
  const internalNote = textOrNull(input.internalNote);
  const path = { tenantId } as const;

  switch (category) {
    case 'Payment':
      return unwrap(
        postApiAccountingTenantsByTenantIdPayments({
          path,
          body: {
            tenantId,
            amount,
            date,
            method,
            bankAccountId,
            description,
            internalNote,
            sourceRef,
          },
        }),
      );
    case 'Security Deposit':
      return unwrap(
        postApiAccountingTenantsByTenantIdDeposits({
          path,
          body: {
            tenantId,
            amount,
            date,
            depositBankId: bankAccountId,
            description,
            internalNote,
            sourceRef,
          },
        }),
      );
    case 'Prepayment':
      return unwrap(
        postApiAccountingTenantsByTenantIdPrepayments({
          path,
          body: {
            tenantId,
            amount,
            date,
            bankAccountId,
            description,
            internalNote,
            sourceRef,
          },
        }),
      );
    case 'Credit':
      return unwrap(
        postApiAccountingTenantsByTenantIdCredits({
          path,
          body: {
            tenantId,
            amount,
            date,
            reason: description ?? 'Credit',
            internalNote,
            sourceRef,
          },
        }),
      );
    default:
      return unwrap(
        postApiAccountingTenantsByTenantIdCharges({
          path,
          body: {
            tenantId,
            amount,
            date,
            kind: CHARGE_KIND[category] ?? 'other',
            description,
            internalNote,
            sourceRef,
          },
        }),
      );
  }
}

/**
 * The owner-facing description `ReversalService` gives a reversal: `Void — {original}`, or a bare
 * `Void` when the original had none. Mirrors the server so the void dialog promises what gets posted.
 */
export function voidDescription(original: string | null | undefined): string {
  return original == null || original.trim() === '' ? 'Void' : `Void — ${original}`;
}

/**
 * Voids a posted entry → a linked reversal (P54 idempotency key; default as-of today server-side). The
 * reason is staff-only: the server stores it as the reversal's internal note, and the owner statement
 * shows `Void — {original description}` instead (#468, ADR-047).
 */
export async function voidEntry(
  entryId: string,
  reason: string,
  sourceRef: string,
): Promise<PostResult> {
  return unwrap(
    postApiAccountingEntriesByEntryIdVoid({
      path: { entryId },
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
  /** Owner-facing: `ApplyDeposit`'s reason prints on the owner statement. */
  reason: string;
  /** Staff-only. */
  internalNote?: string;
  sourceRef: string;
}

export async function applyDeposit(
  tenantId: string,
  input: ApplyDepositInput,
): Promise<PostResult> {
  return unwrap(
    postApiAccountingTenantsByTenantIdDepositApplications({
      path: { tenantId },
      body: {
        tenantId,
        amount: input.amount,
        date: input.date,
        depositBankId: input.depositBankId,
        operatingBankId: input.operatingBankId,
        target: input.target,
        reason: input.reason,
        internalNote: textOrNull(input.internalNote),
        sourceRef: input.sourceRef,
      },
    }),
  );
}

export interface ApplyPrepaymentInput {
  amount: number;
  date: string;
  bankAccountId: string;
  /** Owner-facing: prints on the owner statement. */
  description: string;
  /** Staff-only. */
  internalNote?: string;
  sourceRef: string;
}

export async function applyPrepayment(
  tenantId: string,
  input: ApplyPrepaymentInput,
): Promise<PostResult> {
  return unwrap(
    postApiAccountingTenantsByTenantIdPrepaymentApplications({
      path: { tenantId },
      body: {
        tenantId,
        amount: input.amount,
        date: input.date,
        bankAccountId: input.bankAccountId,
        description: textOrNull(input.description),
        internalNote: textOrNull(input.internalNote),
        sourceRef: input.sourceRef,
      },
    }),
  );
}
