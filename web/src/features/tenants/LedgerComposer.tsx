import { useMutation } from '@tanstack/react-query';
import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { Button, formatMoneyPlain, Icon, Input, Money, Select } from '@/design';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { ErrorAction } from '@/components/ErrorAction';
import { AudienceHint } from '@/components/InternalNote';
import { useBankAccounts } from '@/lib/settings';
import { trackInteraction } from '@/lib/telemetry';
import {
  bankPurposeFor,
  categoryNeedsBank,
  COMPOSER_CHARGE_CATEGORIES,
  type LedgerPostError,
  newSourceRef,
  PAYMENT_METHODS,
  type PostResult,
  submitLedgerEntry,
  useLockedPeriodMessage,
} from './ledgerMutations';

export interface LedgerComposerProps {
  tenantId: string;
  /** Called with the new entry id after a successful post → the page invalidates the ledger + flashes. */
  onPosted: (entryId: string) => void;
  /** Auto-open mode when the page is reached via the palette "Record payment" action. */
  initialMode?: 'payment' | 'charge';
  /**
   * Interactions already spent reaching that auto-open — the ⌘K palette's own two (#408). Applies to
   * the `initialMode` open only; every later open on this page counts from scratch. Absent (a
   * refresh, a bookmark, a hand-typed `?compose=`) means nobody told us, so the count starts here.
   */
  initialInteractions?: number;
  /**
   * What a credit may draw on without an allowance — the ledger read's `owed`, never below zero. Absent
   * while that read is pending or failed: the composer then claims no limit and the server decides.
   * Deliberately not a blocking prerequisite read: nothing from it goes into the request, and without
   * it an excess credit is refused by the server (409, never sent as allowed), so it fails safe.
   */
  owed?: number;
  /** Whether this user may allow a credit larger than `owed` — only an administrator can (#652). */
  canAllowUnappliedCredit?: boolean;
}

type Mode = 'payment' | 'charge';

const LAST_METHOD_KEY = 'lb.lastPaymentMethod';
const todayIso = () => new Date().toISOString().slice(0, 10);

const creditLimitMessage = (owed: number) =>
  `A credit can’t be more than the ${formatMoneyPlain(owed)} the tenant owes. Only an administrator can allow a larger credit.`;

/**
 * The signature M3 interaction (§C.4 / P59): record a payment / add a charge in place, posting through
 * the WP-01 commands, with the new row appearing without navigation. Sensible defaults (category, last-
 * used method, the operating/deposit trust by purpose, today) plus an autofocused amount and Enter-to-
 * post keep a bare payment at ≤ 3 interactions, which `trackInteraction` records on submit. Each open
 * mints a `sourceRef` idempotency key (P54), so a double-submit dedups instead of double-posting.
 */
export function LedgerComposer({
  tenantId,
  onPosted,
  initialMode,
  initialInteractions,
  owed,
  canAllowUnappliedCredit = false,
}: LedgerComposerProps) {
  const lockedMessage = useLockedPeriodMessage();
  const [mode, setMode] = useState<Mode | null>(initialMode ?? null);
  const [amount, setAmount] = useState('');
  const [description, setDescription] = useState('');
  const [internalNote, setInternalNote] = useState('');
  const [method, setMethod] = useState(() => localStorage.getItem(LAST_METHOD_KEY) ?? 'ach');
  const [category, setCategory] = useState<string>('Rent');
  const [date, setDate] = useState(todayIso);
  const [bankId, setBankId] = useState('');
  const [error, setError] = useState<LedgerPostError | null>(null);
  const [allowExcess, setAllowExcess] = useState(false);

  const sourceRef = useRef('');
  const interactions = useRef(1);
  // Whether the operator has opened the composer themselves on this page. It is state set from the
  // toggle handler, never a ref counted inside the effect below: StrictMode double-invokes effects in
  // development, so a counter there reports the palette's auto-open as the operator's second one.
  const [operatorOpened, setOperatorOpened] = useState(false);

  const banks = useBankAccounts(true);
  const banksUnavailable = banks.isPending || banks.isError;
  const open = mode !== null;
  const fieldId = useId();
  const activeCategory = mode === 'payment' ? 'Payment' : category;
  const needsBank = categoryNeedsBank(activeCategory);

  // How far a credit goes past what the tenant owes, in cents, when that is known (#652).
  const isCredit = activeCategory === 'Credit';
  const knownOwed = isCredit ? owed : undefined;
  const parsedAmount = Number.parseFloat(amount);
  const excess =
    knownOwed !== undefined && parsedAmount > knownOwed
      ? Math.round((parsedAmount - knownOwed) * 100) / 100
      : 0;

  const defaultBank = useMemo(() => {
    const purpose = bankPurposeFor(activeCategory);
    return banks.data?.find((bank) => bank.purpose === purpose) ?? banks.data?.[0];
  }, [banks.data, activeCategory]);
  const effectiveBankId = bankId || defaultBank?.id || '';

  // Opening (or switching mode) mints a fresh idempotency key and resets the interaction counter.
  // The auto-open is the palette's when `initialMode` is set and the operator has not opened anything
  // here yet: that flow began before this component existed, so its counter continues (#408).
  useEffect(() => {
    if (!open) return;
    const palette = !operatorOpened && initialMode !== undefined;
    sourceRef.current = newSourceRef();
    interactions.current = (palette ? initialInteractions : undefined) ?? 1;
    setAmount('');
    setDescription('');
    setInternalNote('');
    setBankId('');
    setError(null);
    setAllowExcess(false);
    setDate(todayIso());
  }, [mode, open, operatorOpened, initialMode, initialInteractions]);

  const mutation = useMutation<PostResult, LedgerPostError>({
    mutationFn: () =>
      submitLedgerEntry(tenantId, {
        category: activeCategory,
        amount: Number.parseFloat(amount),
        date,
        description,
        internalNote,
        method,
        bankAccountId: effectiveBankId,
        sourceRef: sourceRef.current,
        allowUnappliedCredit: excess > 0 && allowExcess,
      }),
    onSuccess: (result) => finishPost(result.entryId),
    onError: (post) => {
      // A double-submit/retry already landed (P54): treat it as posted rather than surfacing an error.
      if (post.code === 'duplicate_source_ref' && post.existingEntryId) {
        finishPost(post.existingEntryId);
        return;
      }
      // The bank's month is reconciled (M4 lock): keep the composer open with the move-the-date hint.
      setError(post.code === 'account_period_locked' ? { ...post, message: lockedMessage } : post);
    },
  });

  const finishPost = (entryId: string) => {
    const task = mode === 'payment' ? 'record-payment' : 'add-charge';
    const count = interactions.current + 1; // the open + any choices + this submit
    trackInteraction(task, count, count <= 3);
    if (mode === 'payment') {
      localStorage.setItem(LAST_METHOD_KEY, method);
    }
    onPosted(entryId);
    setMode(null);
  };

  const submit = () => {
    setError(null);
    const value = Number.parseFloat(amount);
    if (!(value > 0)) {
      setError({ message: 'Enter an amount greater than zero.' });
      return;
    }
    if (excess > 0 && knownOwed !== undefined && !(canAllowUnappliedCredit && allowExcess)) {
      setError({
        message: canAllowUnappliedCredit
          ? 'Confirm the larger credit to post it.'
          : creditLimitMessage(knownOwed),
      });
      return;
    }
    if (needsBank && banksUnavailable) {
      // An unread list empties the selector exactly the way an unmade choice does, but only one of
      // them is the operator's to fix. See ApplyModal for why a failed refetch blocks too.
      setError({
        message: banks.isPending
          ? 'Still loading the bank accounts — try again in a moment.'
          : 'The bank accounts couldn’t be loaded, so this can’t be posted yet.',
      });
      return;
    }
    if (needsBank && !effectiveBankId) {
      setError({ message: 'Select a bank account.' });
      return;
    }
    mutation.mutate();
  };

  const onFieldKeyDown = (event: KeyboardEvent) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      submit();
    } else if (event.key === 'Escape') {
      event.preventDefault();
      setMode(null);
    }
  };

  const choose =
    <T,>(setter: (value: T) => void) =>
    (value: T) => {
      interactions.current += 1; // a discrete committing choice (P59)
      setBankId('');
      setter(value);
    };

  const toggle = (next: Mode) => {
    setOperatorOpened(true);
    setMode((current) => (current === next ? null : next));
  };

  return (
    <>
      <div className="pf-ledger-actionbar">
        <div className="row gap10">
          <Button variant="primary" icon="plus" onClick={() => toggle('payment')}>
            Record payment
          </Button>
          <Button variant="default" icon="plus" onClick={() => toggle('charge')}>
            Add charge
          </Button>
        </div>
        <div className="row gap8 t3 fs12">
          <Icon name="refresh" size={14} />
          Ledger updates inline — no navigation
        </div>
      </div>

      {open && (
        <div className={`pf-composer${mode === 'payment' ? ' pay' : ''} pf-fade`}>
          <div className="pf-composer-tag">
            {mode === 'payment' ? 'Record payment' : 'Add charge'}
          </div>
          <div className="pf-composer-grid">
            <label className="pf-composer-field">
              <span>Date</span>
              <Input type="date" value={date} onChange={(e) => setDate(e.target.value)} />
            </label>

            {mode === 'payment' ? (
              <label className="pf-composer-field">
                <span>Method</span>
                <Select
                  value={method}
                  onChange={(e) => choose(setMethod)(e.target.value)}
                  aria-label="Payment method"
                >
                  {PAYMENT_METHODS.map((m) => (
                    <option key={m.value} value={m.value}>
                      {m.label}
                    </option>
                  ))}
                </Select>
              </label>
            ) : (
              <label className="pf-composer-field">
                <span>Charge type</span>
                <Select
                  value={category}
                  onChange={(e) => choose(setCategory)(e.target.value)}
                  aria-label="Charge type"
                >
                  {COMPOSER_CHARGE_CATEGORIES.map((c) => (
                    <option key={c} value={c}>
                      {c}
                    </option>
                  ))}
                </Select>
              </label>
            )}

            <label className="pf-composer-field amount">
              <span>Amount</span>
              <Input
                key={mode}
                autoFocus
                inputMode="decimal"
                placeholder="0.00"
                aria-label="Amount"
                value={amount}
                onChange={(e) => setAmount(e.target.value.replace(/[^0-9.]/g, ''))}
                onKeyDown={onFieldKeyDown}
              />
              {knownOwed !== undefined && (
                <span className="t3 fs12">
                  Tenant owes <Money value={knownOwed} plain />
                </span>
              )}
            </label>

            {needsBank &&
              (banks.isError ? (
                <div className="pf-composer-field col gap6">
                  <ApiErrorNotice
                    error={banks.error}
                    fallback="Couldn’t load the bank accounts."
                    kind="read"
                  />
                  <ErrorAction
                    error={banks.error}
                    onRetry={() => void banks.refetch()}
                    retrying={banks.isFetching}
                  />
                </div>
              ) : (
                <label className="pf-composer-field">
                  <span>Bank</span>
                  <Select
                    value={effectiveBankId}
                    onChange={(e) => choose(setBankId)(e.target.value)}
                    aria-label="Bank account"
                  >
                    {(banks.data ?? []).map((bank) => (
                      <option key={bank.id} value={bank.id}>
                        {bank.name}
                      </option>
                    ))}
                  </Select>
                </label>
              ))}

            {/*
              Two free-text fields, each saying who reads it (#468, ADR-047). The description prints on
              the owner statement — issued statements are immutable and the owner portal serves them
              — so it is never labelled "memo", which reads as private. The internal note stays on
              staff surfaces. A credit's reason is its description, so it carries the same labelling.
            */}
            <div className="pf-composer-field grow">
              <span className="pf-field-labelrow">
                <label htmlFor={`${fieldId}-desc`}>
                  {activeCategory === 'Credit' ? 'Statement reason' : 'Statement description'}
                </label>
                <AudienceHint id={`${fieldId}-desc-hint`} audience="owner" />
              </span>
              <Input
                id={`${fieldId}-desc`}
                aria-describedby={`${fieldId}-desc-hint`}
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                onKeyDown={onFieldKeyDown}
                placeholder={
                  mode === 'payment'
                    ? 'e.g. June rent'
                    : activeCategory === 'Credit'
                      ? 'e.g. Goodwill credit'
                      : 'e.g. Carpet cleaning'
                }
              />
            </div>

            <div className="pf-composer-field grow">
              <span className="pf-field-labelrow">
                <label htmlFor={`${fieldId}-note`}>Internal note</label>
                <AudienceHint id={`${fieldId}-note-hint`} audience="staff" />
              </span>
              <Input
                id={`${fieldId}-note`}
                aria-describedby={`${fieldId}-note-hint`}
                value={internalNote}
                onChange={(e) => setInternalNote(e.target.value)}
                onKeyDown={onFieldKeyDown}
                placeholder="Optional"
              />
            </div>
          </div>
          {excess > 0 && knownOwed !== undefined && (
            <div
              className="pf-composer-limit row gap8 fs13"
              role="status"
              aria-label="Credit limit"
            >
              <Icon name="alert" size={14} />
              {canAllowUnappliedCredit ? (
                <div className="col gap4">
                  <label className="row gap8">
                    <input
                      type="checkbox"
                      className="pf-check"
                      checked={allowExcess}
                      onChange={(e) => {
                        interactions.current += 1;
                        setAllowExcess(e.target.checked);
                      }}
                    />
                    Allow a credit <Money value={excess} plain /> more than the tenant owes
                  </label>
                  <span className="pf-composer-limit-note">
                    The extra <Money value={excess} plain /> leaves the tenant with a credit balance
                    for money they don’t owe.
                  </span>
                </div>
              ) : (
                <span>{creditLimitMessage(knownOwed)}</span>
              )}
            </div>
          )}
          <div className="pf-composer-foot">
            {error ? (
              <ApiErrorNotice error={error} />
            ) : (
              <span className="t3 fs12">
                Posts to the ledger and trust bank instantly — no page change.
              </span>
            )}
            <div className="row gap8">
              <Button variant="ghost" size="sm" onClick={() => setMode(null)}>
                Cancel
              </Button>
              <Button
                variant={mode === 'payment' ? 'primary' : 'default'}
                size="sm"
                icon="check"
                disabled={mutation.isPending}
                onClick={submit}
              >
                {mode === 'payment' ? 'Post payment' : 'Post charge'}
              </Button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}
