import { useMutation } from '@tanstack/react-query';
import { useId, useState, type KeyboardEvent } from 'react';
import type { ApiError } from '@/api';
import { Button, Input, Money } from '@/design';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { AudienceHint } from '@/components/InternalNote';
import { Modal } from '@/components/Modal';
import { num } from '@/lib/directory';
import { LOCKED_PERIOD_MESSAGE } from '@/features/tenants/ledgerMutations';
import { type RefundCheckView, sourceLabel, voidRefundCheck } from './refundChecks';

export interface VoidRefundCheckDialogProps {
  check: RefundCheckView;
  onClose: () => void;
  onVoided: (check: RefundCheckView) => void;
  /** The server refused because the list was stale (already voided, or cleared meanwhile). */
  onStale: () => void;
}

/**
 * Voids a refund check (#473): a reason-required linked reversal that restores the held liability and
 * nets the check out of the register. The check keeps its number on record as voided, so every number
 * drawn on the account stays accounted for; a misprint is handled this way and reissued on the next
 * number. Refused once the check has cleared the bank.
 */
export function VoidRefundCheckDialog({
  check,
  onClose,
  onVoided,
  onStale,
}: VoidRefundCheckDialogProps) {
  const fieldId = useId();
  const [reason, setReason] = useState('');
  const [error, setError] = useState<ApiError | null>(null);
  const number = num(check.checkNumber);

  const mutation = useMutation<RefundCheckView, ApiError>({
    mutationFn: () => voidRefundCheck(check.id, reason.trim()),
    onSuccess: onVoided,
    onError: (err) => {
      if (err.code === 'already_reversed') {
        onStale();
        setError({ ...err, message: `Check #${number} has already been voided.` });
      } else if (err.code === 'refund_check_cleared') {
        onStale();
        setError({
          ...err,
          message: `Check #${number} has cleared the bank, so it can no longer be voided.`,
        });
      } else if (err.code === 'account_period_locked') {
        setError({ ...err, message: LOCKED_PERIOD_MESSAGE });
      } else {
        setError(err);
      }
    },
  });

  const confirm = () => {
    if (mutation.isPending) return;
    if (reason.trim() === '') {
      setError({ message: 'A reason is required to void a check.' });
      document.getElementById(`${fieldId}-reason`)?.focus();
      return;
    }
    setError(null);
    mutation.mutate();
  };

  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      confirm();
    }
  };

  return (
    <Modal
      title={`Void check #${number}`}
      onClose={onClose}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button variant="primary" icon="x" disabled={mutation.isPending} onClick={confirm}>
            {mutation.isPending ? 'Voiding…' : 'Void check'}
          </Button>
        </>
      }
    >
      <div className="pf-modal-body col gap12">
        <p className="t3 fs13">
          Check <b style={{ color: 'var(--text)' }}>#{number}</b> to {check.payeeName} for{' '}
          <Money value={num(check.amount)} plain />. Voiding posts a linked reversal that restores
          the {sourceLabel(check.source).toLowerCase()} held for the tenant, and the check and its
          reversal leave the register’s outstanding items together. The number stays on record as
          voided; to replace a misprint, issue a new check on the next number.
        </p>
        <div className="col gap6">
          <span className="pf-field-labelrow">
            <label className="pf-eyebrow" htmlFor={`${fieldId}-reason`}>
              Reason (internal note)
            </label>
            <AudienceHint id={`${fieldId}-reason-hint`} audience="staff" />
          </span>
          <Input
            id={`${fieldId}-reason`}
            aria-describedby={`${fieldId}-reason-hint`}
            maxLength={500}
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            onKeyDown={onKeyDown}
            placeholder="e.g. misprinted on the stock"
          />
        </div>
        <ApiErrorNotice error={error} />
      </div>
    </Modal>
  );
}
