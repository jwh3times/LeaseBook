import { useMutation } from '@tanstack/react-query';
import { useRef, useState, type KeyboardEvent } from 'react';
import { Button, Input, Select } from '@/design';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { Modal } from '@/components/Modal';
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
 * Guided deposit/prepayment application (§C.4). Resolves the deposit/operating trust banks by purpose,
 * applies through the WP-01 commands, and surfaces the engine guards in place (P51): an over-receivable
 * application (`insufficient_receivable`) or an over-held one (`insufficient_liability`) renders an
 * inline warning and keeps the modal open so the user lowers the amount.
 */
export function ApplyModal({ tenantId, initialKind, onClose, onApplied }: ApplyModalProps) {
  const [kind, setKind] = useState<Kind>(initialKind);
  const [amount, setAmount] = useState('');
  const [target, setTarget] = useState('against-charges');
  const [reason, setReason] = useState('');
  const [error, setError] = useState<LedgerPostError | null>(null);
  const sourceRef = useRef(newSourceRef());

  const banks = useBankAccounts(true);
  const depositBank = banks.data?.find((bank) => bank.purpose === 'deposit') ?? banks.data?.[0];
  const operatingBank = banks.data?.find((bank) => bank.purpose === 'trust') ?? banks.data?.[0];

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
          reason: reason.trim() === '' ? 'Applied' : reason.trim(),
          sourceRef: sourceRef.current,
        });
      }
      return applyPrepayment(tenantId, {
        amount: value,
        date,
        bankAccountId: operatingBank!.id,
        memo: reason,
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
          <Button variant="primary" icon="check" disabled={mutation.isPending} onClick={submit}>
            Apply
          </Button>
        </>
      }
    >
      <div className="pf-modal-body col gap12">
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

        {depositBank && operatingBank && (
          <p className="t3 fs12">
            {kind === 'deposit'
              ? `From ${depositBank.name} → ${operatingBank.name}`
              : `From ${operatingBank.name}`}
          </p>
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

        <label className="col gap6">
          <span className="pf-eyebrow">Reason</span>
          <Input
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            onKeyDown={onKeyDown}
            placeholder="e.g. move-out settlement"
            aria-label="Reason"
          />
        </label>

        <ApiErrorNotice error={error} />
      </div>
    </Modal>
  );
}
