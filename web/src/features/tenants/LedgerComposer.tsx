import { useMutation } from '@tanstack/react-query';
import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { Button, Icon, Input, Select } from '@/design';
import { useBankAccounts } from '@/lib/settings';
import { trackInteraction } from '@/lib/telemetry';
import {
  bankPurposeFor,
  categoryNeedsBank,
  COMPOSER_CHARGE_CATEGORIES,
  type LedgerPostError,
  LOCKED_PERIOD_MESSAGE,
  newSourceRef,
  PAYMENT_METHODS,
  type PostResult,
  submitLedgerEntry,
} from './ledgerMutations';

export interface LedgerComposerProps {
  tenantId: string;
  /** Called with the new entry id after a successful post → the page invalidates the ledger + flashes. */
  onPosted: (entryId: string) => void;
  /** Auto-open mode when the page is reached via the palette "Record payment" action. */
  initialMode?: 'payment' | 'charge';
}

type Mode = 'payment' | 'charge';

const LAST_METHOD_KEY = 'lb.lastPaymentMethod';
const todayIso = () => new Date().toISOString().slice(0, 10);

/**
 * The signature M3 interaction (§C.4 / P59): record a payment / add a charge in place, posting through
 * the WP-01 commands, with the new row appearing without navigation. Sensible defaults (category, last-
 * used method, the operating/deposit trust by purpose, today) plus an autofocused amount and Enter-to-
 * post keep a bare payment at ≤ 3 interactions, which `trackInteraction` records on submit. Each open
 * mints a `sourceRef` idempotency key (P54), so a double-submit dedups instead of double-posting.
 */
export function LedgerComposer({ tenantId, onPosted, initialMode }: LedgerComposerProps) {
  const [mode, setMode] = useState<Mode | null>(initialMode ?? null);
  const [amount, setAmount] = useState('');
  const [memo, setMemo] = useState('');
  const [method, setMethod] = useState(() => localStorage.getItem(LAST_METHOD_KEY) ?? 'ach');
  const [category, setCategory] = useState<string>('Rent');
  const [date, setDate] = useState(todayIso);
  const [bankId, setBankId] = useState('');
  const [error, setError] = useState<string | null>(null);

  const sourceRef = useRef('');
  const interactions = useRef(1);

  const banks = useBankAccounts();
  const open = mode !== null;
  const activeCategory = mode === 'payment' ? 'Payment' : category;
  const needsBank = categoryNeedsBank(activeCategory);

  const defaultBank = useMemo(() => {
    const purpose = bankPurposeFor(activeCategory);
    return banks.data?.find((bank) => bank.purpose === purpose) ?? banks.data?.[0];
  }, [banks.data, activeCategory]);
  const effectiveBankId = bankId || defaultBank?.id || '';

  // Opening (or switching mode) mints a fresh idempotency key and resets the interaction counter.
  useEffect(() => {
    if (!open) return;
    sourceRef.current = newSourceRef();
    interactions.current = 1;
    setAmount('');
    setMemo('');
    setBankId('');
    setError(null);
    setDate(todayIso());
  }, [mode, open]);

  const mutation = useMutation<PostResult, LedgerPostError>({
    mutationFn: () =>
      submitLedgerEntry(tenantId, {
        category: activeCategory,
        amount: Number.parseFloat(amount),
        date,
        memo,
        method,
        bankAccountId: effectiveBankId,
        sourceRef: sourceRef.current,
      }),
    onSuccess: (result) => finishPost(result.entryId),
    onError: (post) => {
      // A double-submit/retry already landed (P54): treat it as posted rather than surfacing an error.
      if (post.code === 'duplicate_source_ref' && post.existingEntryId) {
        finishPost(post.existingEntryId);
        return;
      }
      // The bank's month is reconciled (M4 lock): keep the composer open with the move-the-date hint.
      setError(post.code === 'account_period_locked' ? LOCKED_PERIOD_MESSAGE : post.message);
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
      setError('Enter an amount greater than zero.');
      return;
    }
    if (needsBank && !effectiveBankId) {
      setError('Select a bank account.');
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

  const toggle = (next: Mode) => setMode((current) => (current === next ? null : next));

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
            </label>

            {needsBank && (
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
            )}

            <label className="pf-composer-field grow">
              <span>{activeCategory === 'Credit' ? 'Reason' : 'Memo'}</span>
              <Input
                value={memo}
                onChange={(e) => setMemo(e.target.value)}
                onKeyDown={onFieldKeyDown}
                placeholder={mode === 'payment' ? 'e.g. June rent' : 'Description'}
              />
            </label>
          </div>
          <div className="pf-composer-foot">
            {error ? (
              <span className="pf-composer-error" role="alert">
                {error}
              </span>
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
