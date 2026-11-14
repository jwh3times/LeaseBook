import { useMutation } from '@tanstack/react-query';
import { useId, useMemo, useRef, useState } from 'react';
import type { ApiError } from '@/api';
import { Button, Input, Money, Select } from '@/design';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { ErrorAction } from '@/components/ErrorAction';
import { AudienceHint } from '@/components/InternalNote';
import { Modal } from '@/components/Modal';
import { num } from '@/lib/directory';
import { reportFirstInvalidField } from '@/lib/fieldConstraints';
import { orgTodayUnavailableMessage, useOrgToday } from '@/lib/orgCalendar';
import { useLockedPeriodMessage } from '@/features/tenants/ledgerMutations';
import { centsToAmount, newIssueKey, parseAmountCents } from '@/features/refundChecks/refundChecks';
import {
  type BankAdjustmentKind,
  recordBankAdjustment,
  useBankBalances,
  useHeldFees,
} from './banking';

export interface BankAdjustmentDialogProps {
  bankAccountId: string;
  bankName: string;
  /**
   * Whether the open account is a trust bank: only a trust bank offers a transfer (#553), a PM
   * contribution (#720) or a sweep of its held fees (#743).
   */
  isTrust: boolean;
  onClose: () => void;
  /**
   * Posted: the page refreshes the open account, and a transfer's or a sweep's destination, and says
   * what was recorded. `toBankAccountId` is null for anything but a transfer or a sweep.
   */
  onPosted: (posted: {
    kind: BankAdjustmentKind;
    amount: number;
    toBankAccountId: string | null;
  }) => void;
}

type FieldName = 'amount' | 'date' | 'to';

interface FormError extends ApiError {
  field?: FieldName;
}

const AMOUNT_PATTERN = '\\d{1,7}(\\.\\d{0,2})?';

/** What each kind is called on its choice, its dialog, its save button and its default description. */
const KINDS: Record<
  BankAdjustmentKind,
  { label: string; title: string; action: string; placeholder: string }
> = {
  fee: {
    label: 'Bank fee',
    title: 'Record a bank fee',
    action: 'Record fee',
    placeholder: 'Bank fee',
  },
  transfer: {
    label: 'Transfer to another trust account',
    title: 'Transfer to another trust account',
    action: 'Record transfer',
    placeholder: 'Transfer',
  },
  contribution: {
    label: 'PM contribution',
    title: 'Record a PM contribution',
    action: 'Record contribution',
    placeholder: 'PM contribution',
  },
  sweep: {
    label: 'Sweep PM fees',
    title: 'Sweep PM fees to the PM operating account',
    action: 'Record sweep',
    placeholder: 'PM fee sweep',
  },
};

/**
 * Records a bank fee, or a transfer to another trust account, from the bank register (#553). Both are
 * paid from the PM's management fees held in the account, and the server refuses one that would take
 * them below zero (`pm_fees_insufficient`), so that refusal is shown beside the amount in the server's
 * own words, which say how much is held and that a PM contribution covers it. A PM contribution (#720)
 * is the property manager's own money paid into a trust account to join those held fees. A sweep (#743)
 * moves those held fees out of trust into the PM operating account: its amount starts at the most the
 * server will accept on its date, the lowest the fees held there are from that date on, and follows the
 * date until the operator types one. Interest is not offered until #307 settles who it belongs to.
 * <para>
 * It moves money, so like the other money dialogs it has no form and saves only from its button, never
 * from Enter, and the button runs each field's own constraints before anything is sent (#711). One
 * source reference is minted per open, so a retried
 * save that already posted is recognised as posted rather than recorded twice.
 * </para>
 */
export function BankAdjustmentDialog({
  bankAccountId,
  bankName,
  isTrust,
  onClose,
  onPosted,
}: BankAdjustmentDialogProps) {
  const fieldId = useId();
  const bodyRef = useRef<HTMLDivElement>(null);
  const sourceRef = useRef(`bank-adjustment:${newIssueKey()}`);
  const lockedMessage = useLockedPeriodMessage();
  const orgToday = useOrgToday();
  const balances = useBankBalances();

  const [kind, setKind] = useState<BankAdjustmentKind>('fee');
  const [amount, setAmount] = useState('');
  // False until the operator types an amount: a sweep's amount is the held-fees floor at its date.
  const [amountEdited, setAmountEdited] = useState(false);
  // null until the operator types one: the entry is dated the organization's today (#642).
  const [dateEdit, setDate] = useState<string | null>(null);
  const date = dateEdit ?? (orgToday.status === 'ready' ? orgToday.today : '');
  const [toChoice, setTo] = useState('');
  const [description, setDescription] = useState('');
  const [internalNote, setInternalNote] = useState('');
  const [error, setError] = useState<FormError | null>(null);

  const transfer = kind === 'transfer';
  const sweep = kind === 'sweep';
  const namesDestination = transfer || sweep;

  // A transfer goes to another trust account; a sweep to a PM operating account, never a trust one.
  const destinations = useMemo(
    () =>
      !balances.isSuccess
        ? []
        : kind === 'transfer'
          ? balances.data.filter((bank) => bank.isTrust && bank.bankAccountId !== bankAccountId)
          : kind === 'sweep'
            ? balances.data.filter((bank) => !bank.isTrust)
            : [],
    [balances.isSuccess, balances.data, bankAccountId, kind],
  );
  // One destination needs no choosing; several start unchosen so money never lands by default. A
  // choice made for the other kind is not one of this kind's accounts, so it is not kept.
  const chosen = destinations.some((bank) => bank.bankAccountId === toChoice) ? toChoice : '';
  const to = chosen || (destinations.length === 1 ? (destinations[0]?.bankAccountId ?? '') : '');
  const noDestination = namesDestination && balances.isSuccess && destinations.length === 0;

  // A transfer's or a sweep's destinations come from the account list; a failed or pending read of
  // it — or a failed refetch that still holds rows naming a since-deactivated account — blocks the write.
  const destinationsUnavailable = namesDestination && (balances.isPending || balances.isError);
  // What a sweep dated `date` may take: the server refuses anything past this floor.
  const heldFees = useHeldFees(bankAccountId, date, { enabled: sweep });
  const floorCents = heldFees.isSuccess
    ? Math.max(0, Math.round(num(heldFees.data.floor) * 100))
    : null;
  const prefill = floorCents ? centsToAmount(floorCents).toFixed(2) : '';
  const shownAmount = sweep && !amountEdited ? prefill : amount;
  // The amount a sweep offers comes from that read, so like the destinations a failed or pending read
  // of it blocks the write. A blank date reads nothing; the date field's own constraint names it.
  const heldFeesUnavailable =
    sweep && (heldFees.isError || (heldFees.fetchStatus === 'fetching' && !heldFees.isSuccess));
  const blocked =
    orgToday.status !== 'ready' || destinationsUnavailable || noDestination || heldFeesUnavailable;

  const amountValue = centsToAmount(parseAmountCents(shownAmount) ?? 0);

  const mutation = useMutation<{ entryId: string }, ApiError, void>({
    mutationFn: () =>
      recordBankAdjustment(bankAccountId, {
        kind,
        amount: amountValue,
        date,
        description: description.trim() === '' ? null : description.trim(),
        toBankAccountId: namesDestination ? to : null,
        sourceRef: sourceRef.current,
        internalNote: internalNote.trim() === '' ? null : internalNote.trim(),
      }),
    onSuccess: () => posted(),
    onError: (err) => {
      switch (err.code) {
        // The source reference is this dialog's own, so a duplicate is this save, already posted.
        case 'duplicate_source_ref':
          posted();
          break;
        case 'pm_fees_insufficient':
          setError({ ...err, field: 'amount' });
          break;
        case 'transfer_bank_not_trust':
        case 'sweep_bank_invalid':
          setError({ ...err, field: 'to' });
          break;
        case 'account_period_locked':
          setError({ ...err, field: 'date', message: lockedMessage });
          break;
        case 'period_closed':
          setError({ ...err, field: 'date', message: `${err.message} Date it in an open period.` });
          break;
        case 'posting_date_in_future':
          setError({ ...err, field: 'date' });
          break;
        default:
          setError(err);
      }
    },
  });

  const posted = () =>
    onPosted({ kind, amount: amountValue, toBankAccountId: namesDestination ? to : null });

  const fail = (field: FieldName, message: string) => {
    setError({ field, message });
    document.getElementById(`${fieldId}-${field}`)?.focus();
  };

  const save = () => {
    if (mutation.isPending) return;
    // Each field's own constraints first, in order: the browser names the first that fails.
    if (!reportFirstInvalidField(bodyRef.current)) return;
    if (orgToday.status !== 'ready') return fail('date', orgTodayUnavailableMessage(orgToday));
    const cents = parseAmountCents(shownAmount);
    if (cents === null || cents <= 0) {
      return fail('amount', 'Enter an amount greater than zero, with at most two decimal places.');
    }
    if (transfer && to === '') return fail('to', 'Choose the trust account it goes to.');
    if (sweep && to === '') return fail('to', 'Choose the PM operating account it goes to.');
    setError(null);
    mutation.mutate();
  };

  const describedBy = (field: FieldName, ...more: string[]) =>
    [error?.field === field ? `${fieldId}-error` : '', ...more].filter(Boolean).join(' ') ||
    undefined;

  const selectedDestination = destinations.find((bank) => bank.bankAccountId === to);

  return (
    <Modal
      title={KINDS[kind].title}
      onClose={onClose}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="primary"
            icon="check"
            disabled={mutation.isPending || blocked}
            aria-busy={mutation.isPending || undefined}
            onClick={save}
          >
            {mutation.isPending ? 'Recording…' : KINDS[kind].action}
          </Button>
        </>
      }
    >
      <div ref={bodyRef} className="pf-modal-body col gap12">
        {kind === 'contribution' ? (
          <p className="t3 fs13">
            The property manager’s own money paid into <b>{bankName}</b>. It joins the management
            fees held there, so a bank fee or a transfer dated on or after it can be paid from it.
            It is never counted as fee income.
          </p>
        ) : sweep ? (
          <p className="t3 fs13">
            Moves the management fees the property manager has earned out of <b>{bankName}</b> into
            its own operating account. Owner and tenant funds are never swept, so the amount can be
            no more than the fees held there from its date on.
          </p>
        ) : (
          <p className="t3 fs13">
            Paid from the management fees held in <b>{bankName}</b>. Owner and tenant funds are
            never used, so the amount can be no more than the fees held there from its date on.
          </p>
        )}

        {isTrust && (
          <fieldset className="pf-adjust-kinds">
            <legend className="pf-eyebrow">What to record</legend>
            {(Object.keys(KINDS) as BankAdjustmentKind[]).map((choice) => (
              <label key={choice} className="row gap6">
                <input
                  type="radio"
                  name={`${fieldId}-kind`}
                  value={choice}
                  checked={kind === choice}
                  onChange={() => {
                    setKind(choice);
                    // A sweep starts from what may be swept, whatever was typed for another kind.
                    if (choice === 'sweep') setAmountEdited(false);
                    setError(null);
                  }}
                />
                {KINDS[choice].label}
              </label>
            ))}
          </fieldset>
        )}

        <div className="pf-adjust-grid">
          <div className="col gap6">
            <label className="pf-eyebrow" htmlFor={`${fieldId}-amount`}>
              Amount
            </label>
            <Input
              id={`${fieldId}-amount`}
              inputMode="decimal"
              placeholder="0.00"
              required
              pattern={AMOUNT_PATTERN}
              title="An amount in dollars, with at most two decimal places"
              value={shownAmount}
              aria-invalid={error?.field === 'amount' || undefined}
              aria-describedby={describedBy('amount', sweep ? `${fieldId}-held` : '')}
              onChange={(e) => {
                setAmount(e.target.value.replace(/[^0-9.]/g, ''));
                setAmountEdited(true);
              }}
            />
          </div>
          <div className="col gap6">
            <label className="pf-eyebrow" htmlFor={`${fieldId}-date`}>
              Date
            </label>
            <Input
              id={`${fieldId}-date`}
              type="date"
              required
              max={orgToday.status === 'ready' ? orgToday.today : undefined}
              value={date}
              aria-invalid={error?.field === 'date' || undefined}
              aria-describedby={describedBy('date')}
              onChange={(e) => setDate(e.target.value)}
            />
          </div>
        </div>

        {orgToday.status === 'pending' && (
          <span className="t3 fs12" role="status">
            Loading today’s date…
          </span>
        )}
        {orgToday.status === 'error' && (
          <div className="col gap6">
            <ApiErrorNotice
              error={orgToday.error}
              fallback="Couldn’t load the organization’s settings."
              kind="read"
            />
            <ErrorAction
              error={orgToday.error}
              onRetry={orgToday.retry}
              retrying={orgToday.retrying}
            />
          </div>
        )}

        {sweep && (
          <div id={`${fieldId}-held`} className="col gap6">
            {heldFees.isError ? (
              <>
                <ApiErrorNotice
                  error={heldFees.error}
                  fallback="Couldn’t load the fees held in this account."
                  kind="read"
                />
                <ErrorAction
                  error={heldFees.error}
                  onRetry={() => void heldFees.refetch()}
                  retrying={heldFees.isFetching}
                />
              </>
            ) : heldFees.isSuccess ? (
              floorCents ? (
                <span className="t3 fs12">
                  Up to <Money value={num(heldFees.data.floor)} plain /> can be swept on this date,
                  the least held here from then on. Held here today:{' '}
                  <Money value={num(heldFees.data.held)} plain />.
                </span>
              ) : (
                <span className="t3 fs12" role="status">
                  No management fees are held here from this date on, so there is nothing to sweep.
                </span>
              )
            ) : heldFees.fetchStatus === 'fetching' ? (
              <>
                <span className="t3 fs12" role="status">
                  Loading the fees held here…
                </span>
                <div className="pf-skeleton" style={{ height: 20 }} />
              </>
            ) : (
              <span className="t3 fs12">Enter a date to see the fees held here from it on.</span>
            )}
          </div>
        )}

        {namesDestination &&
          (balances.isError ? (
            <div className="col gap6">
              <ApiErrorNotice
                error={balances.error}
                fallback={
                  sweep ? 'Couldn’t load the bank accounts.' : 'Couldn’t load the trust accounts.'
                }
                kind="read"
              />
              <ErrorAction
                error={balances.error}
                onRetry={() => void balances.refetch()}
                retrying={balances.isFetching}
              />
            </div>
          ) : balances.isPending ? (
            <div className="col gap6">
              <span className="t3 fs12">
                {sweep ? 'Loading the bank accounts…' : 'Loading the trust accounts…'}
              </span>
              <div className="pf-skeleton" style={{ height: 20 }} />
            </div>
          ) : noDestination ? (
            <p className="t3 fs13" role="status">
              {sweep
                ? 'There is no PM operating account to sweep to. Add one in Settings first.'
                : 'There is no other trust account to transfer to. Add one in Settings first.'}
            </p>
          ) : (
            <div className="col gap6">
              <label className="pf-eyebrow" htmlFor={`${fieldId}-to`}>
                To account
              </label>
              <Select
                id={`${fieldId}-to`}
                required
                value={to}
                aria-invalid={error?.field === 'to' || undefined}
                aria-describedby={describedBy('to')}
                onChange={(e) => setTo(e.target.value)}
              >
                {destinations.length > 1 && (
                  <option value="">
                    {sweep ? 'Choose a PM operating account' : 'Choose a trust account'}
                  </option>
                )}
                {destinations.map((bank) => (
                  <option key={bank.bankAccountId} value={bank.bankAccountId}>
                    {bank.name}
                  </option>
                ))}
              </Select>
              {selectedDestination && (
                <span className="t3 fs12">
                  Book balance there: <Money value={num(selectedDestination.book)} plain />
                </span>
              )}
            </div>
          ))}

        <div className="col gap6">
          <label className="pf-eyebrow" htmlFor={`${fieldId}-description`}>
            Description
          </label>
          <Input
            id={`${fieldId}-description`}
            maxLength={200}
            placeholder={KINDS[kind].placeholder}
            value={description}
            onChange={(e) => setDescription(e.target.value)}
          />
        </div>
        <div className="col gap6">
          <span className="pf-field-labelrow">
            <label className="pf-eyebrow" htmlFor={`${fieldId}-internalNote`}>
              Internal note
            </label>
            <AudienceHint id={`${fieldId}-internalNote-hint`} audience="staff" />
          </span>
          <Input
            id={`${fieldId}-internalNote`}
            maxLength={500}
            placeholder="Optional"
            value={internalNote}
            aria-describedby={`${fieldId}-internalNote-hint`}
            onChange={(e) => setInternalNote(e.target.value)}
          />
        </div>

        <div id={`${fieldId}-error`}>
          <ApiErrorNotice error={error} />
        </div>
      </div>
    </Modal>
  );
}
