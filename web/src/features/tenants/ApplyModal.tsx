import { useMutation } from '@tanstack/react-query';
import { useId, useRef, useState, type KeyboardEvent } from 'react';
import { Button, formatMoneyPlain, Input, Select } from '@/design';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { ErrorAction } from '@/components/ErrorAction';
import { AudienceHint } from '@/components/InternalNote';
import { Modal } from '@/components/Modal';
import { useRefundCheckOptions } from '@/features/refundChecks/refundChecks';
import { num } from '@/lib/directory';
import { useBankAccounts } from '@/lib/settings';
import {
  applyDeposit,
  applyPrepayment,
  type LedgerPostError,
  LOCKED_PERIOD_MESSAGE,
  newSourceRef,
  type PostResult,
} from './ledgerMutations';

type Kind = 'deposit' | 'prepayment';

interface ApplyModalProps {
  tenantId: string;
  initialKind: Kind;
  onClose: () => void;
  onApplied: (entryId: string) => void;
}

const todayIso = () => new Date().toISOString().slice(0, 10);

/**
 * Guided deposit/prepayment application (§C.4). A deposit resolves the deposit/operating trust banks by
 * purpose; a prepayment names no bank, because only the server knows which bank holds it (#475) — when
 * it sits in several (`prepayment_bank_ambiguous`) the modal offers those banks and resubmits with the
 * chosen one. Applies through the WP-01 commands and surfaces the engine guards in place (P51): an
 * over-receivable application (`insufficient_receivable`) or an over-held one (`insufficient_liability`)
 * renders an inline warning and keeps the modal open so the user lowers the amount.
 */
export function ApplyModal({ tenantId, initialKind, onClose, onApplied }: ApplyModalProps) {
  const [kind, setKind] = useState<Kind>(initialKind);
  const [amount, setAmount] = useState('');
  const [target, setTarget] = useState('against-charges');
  // Owner-facing: a deposit application's reason and a prepayment application's description both
  // print on the owner statement (#468, ADR-047). The internal note stays on staff surfaces.
  const [description, setDescription] = useState('');
  const [internalNote, setInternalNote] = useState('');
  const fieldId = useId();
  const [error, setError] = useState<LedgerPostError | null>(null);
  const sourceRef = useRef(newSourceRef());
  // Set once the server reports the prepaid credit spans banks; until then the server picks the bank.
  const [needsBank, setNeedsBank] = useState(false);
  const [chosenBank, setChosenBank] = useState<string | null>(null);
  const funds = useRefundCheckOptions(tenantId, { enabled: needsBank });
  const prepaymentBanks = (funds.data?.funds ?? []).filter((fund) => fund.source === 'prepayment');
  const prepaymentBank = needsBank
    ? (chosenBank ?? prepaymentBanks[0]?.bankAccountId ?? null)
    : null;

  const banks = useBankAccounts(true);
  const depositBank = banks.data?.find((bank) => bank.purpose === 'deposit') ?? banks.data?.[0];
  const operatingBank = banks.data?.find((bank) => bank.purpose === 'trust') ?? banks.data?.[0];

  /**
   * `isError` blocks even when cached rows are still in hand: a failed refetch means the list may
   * name a bank that has since been deactivated, and this modal posts money against whichever id it
   * resolves. A stale read is not a good enough basis for that.
   */
  const banksUnavailable = banks.isPending || banks.isError;
  const bankName = (id: string) =>
    banks.data?.find((bank) => bank.id === id)?.name ?? 'Trust account';

  const mutation = useMutation<PostResult, LedgerPostError>({
    mutationFn: () => {
      const value = Number.parseFloat(amount);
      const date = todayIso();
      if (kind === 'deposit') {
        return applyDeposit(tenantId, {
          amount: value,
          date,
          depositBankId: depositBank!.id,
          operatingBankId: operatingBank!.id,
          target,
          reason: description.trim() === '' ? 'Applied' : description.trim(),
          internalNote,
          sourceRef: sourceRef.current,
        });
      }
      return applyPrepayment(tenantId, {
        amount: value,
        date,
        bankAccountId: prepaymentBank,
        description,
        internalNote,
        sourceRef: sourceRef.current,
      });
    },
    onSuccess: (result) => onApplied(result.entryId),
    onError: (err) => {
      if (err.code === 'duplicate_source_ref' && err.existingEntryId) {
        onApplied(err.existingEntryId);
      } else if (err.code === 'account_period_locked') {
        // The trust bank's month is reconciled (M4 lock): keep the modal open with the move-the-date hint.
        setError({ ...err, message: LOCKED_PERIOD_MESSAGE });
      } else if (err.code === 'prepayment_bank_ambiguous') {
        // The server's message asks for the choice; the picker below supplies it.
        setNeedsBank(true);
        if (needsBank) void funds.refetch();
        setError(err);
      } else {
        // insufficient_receivable / insufficient_liability messages already name the limit hit.
        setError(err);
      }
    },
  });

  const submit = () => {
    const value = Number.parseFloat(amount);
    if (!(value > 0)) {
      setError({ message: 'Enter an amount greater than zero.' });
      return;
    }
    if (banksUnavailable) {
      // Both banks resolve out of `banks.data`, so an unread list looks exactly like an org with no
      // trust bank. Naming the wrong one sends the operator to Settings to fix nothing.
      setError({
        message: banks.isPending
          ? 'Still loading the trust accounts — try again in a moment.'
          : 'The trust accounts couldn’t be loaded, so nothing can be applied yet.',
      });
      return;
    }
    if (!depositBank || !operatingBank) {
      setError({ message: 'No trust bank is configured for this org.' });
      return;
    }
    setError(null);
    mutation.mutate();
  };

  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      submit();
    }
  };

  return (
    <Modal
      title="Apply held funds"
      onClose={onClose}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="primary"
            icon="check"
            disabled={mutation.isPending || banksUnavailable || (needsBank && funds.isPending)}
            onClick={submit}
          >
            Apply
          </Button>
        </>
      }
    >
      <div className="pf-modal-body col gap12">
        {banks.isError && (
          <div className="col gap6">
            <ApiErrorNotice
              error={banks.error}
              fallback="Couldn’t load the trust accounts."
              kind="read"
            />
            <ErrorAction
              error={banks.error}
              onRetry={() => void banks.refetch()}
              retrying={banks.isFetching}
            />
          </div>
        )}
        <label className="col gap6">
          <span className="pf-eyebrow">Source</span>
          <Select
            value={kind}
            onChange={(e) => setKind(e.target.value as Kind)}
            aria-label="Source"
          >
            <option value="deposit">Security deposit</option>
            <option value="prepayment">Prepayment</option>
          </Select>
        </label>

        {kind === 'deposit' && depositBank && operatingBank && (
          <p className="t3 fs12">{`From ${depositBank.name} → ${operatingBank.name}`}</p>
        )}

        {kind === 'prepayment' && needsBank && prepaymentBanks.length > 0 && (
          <label className="col gap6">
            <span className="pf-eyebrow">Apply from</span>
            <Select
              value={prepaymentBank ?? ''}
              onChange={(e) => setChosenBank(e.target.value)}
              aria-label="Apply from"
            >
              {prepaymentBanks.map((fund) => (
                <option key={fund.bankAccountId} value={fund.bankAccountId}>
                  {`${bankName(fund.bankAccountId)} — ${formatMoneyPlain(num(fund.held))}`}
                </option>
              ))}
            </Select>
          </label>
        )}

        <label className="col gap6">
          <span className="pf-eyebrow">Amount</span>
          <Input
            inputMode="decimal"
            placeholder="0.00"
            aria-label="Amount"
            value={amount}
            onChange={(e) => setAmount(e.target.value.replace(/[^0-9.]/g, ''))}
            onKeyDown={onKeyDown}
          />
        </label>

        {kind === 'deposit' && (
          <label className="col gap6">
            <span className="pf-eyebrow">Apply to</span>
            <Select
              value={target}
              onChange={(e) => setTarget(e.target.value)}
              aria-label="Apply to"
            >
              <option value="against-charges">The tenant's open charges</option>
              <option value="to-owner-income">Owner income (damages)</option>
            </Select>
          </label>
        )}

        <div className="col gap6">
          <span className="pf-field-labelrow">
            <label className="pf-eyebrow" htmlFor={`${fieldId}-desc`}>
              Statement description
            </label>
            <AudienceHint id={`${fieldId}-desc-hint`} audience="owner" />
          </span>
          <Input
            id={`${fieldId}-desc`}
            aria-describedby={`${fieldId}-desc-hint`}
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            onKeyDown={onKeyDown}
            placeholder="e.g. Move-out settlement"
          />
        </div>

        <div className="col gap6">
          <span className="pf-field-labelrow">
            <label className="pf-eyebrow" htmlFor={`${fieldId}-note`}>
              Internal note
            </label>
            <AudienceHint id={`${fieldId}-note-hint`} audience="staff" />
          </span>
          <Input
            id={`${fieldId}-note`}
            aria-describedby={`${fieldId}-note-hint`}
            value={internalNote}
            onChange={(e) => setInternalNote(e.target.value)}
            onKeyDown={onKeyDown}
            placeholder="Optional"
          />
        </div>

        <ApiErrorNotice error={error} />
      </div>
    </Modal>
  );
}
