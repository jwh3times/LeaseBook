import { useMutation } from '@tanstack/react-query';
import { useId, useState, type KeyboardEvent } from 'react';
import { Button, Input } from '@/design';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { Modal } from '@/components/Modal';
import { type BankingError, type ReconciliationView, unlockReconciliation } from './banking';

interface UnlockReconciliationDialogProps {
  reconciliationId: string;
  /** The month as the history row names it, e.g. `Sep 2026`. */
  period: string;
  onClose: () => void;
  onUnlocked: () => void;
}

/**
 * Unlock confirmation (#554, ADR-014 §4): a PMAdmin reopens a finalized month with a required reason,
 * which the audit log keeps with the acting user. A refusal stays inline and the dialog stays open, so
 * the reason is not lost.
 */
export function UnlockReconciliationDialog({
  reconciliationId,
  period,
  onClose,
  onUnlocked,
}: UnlockReconciliationDialogProps) {
  const fieldId = useId();
  const [reason, setReason] = useState('');
  const [error, setError] = useState<BankingError | null>(null);

  const mutation = useMutation<ReconciliationView, BankingError>({
    mutationFn: () => unlockReconciliation(reconciliationId, reason.trim()),
    onSuccess: () => onUnlocked(),
    onError: (err) =>
      // The policy's 403 has no body, so on its own it says only that the unlock failed, not why.
      setError(
        err.status === 403
          ? { ...err, message: 'Only an administrator can unlock a reconciliation.' }
          : err,
      ),
  });

  const confirm = () => {
    if (reason.trim() === '') {
      setError({ message: 'A reason is required to unlock a reconciled month.' });
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
      title={`Unlock ${period}`}
      onClose={onClose}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button variant="primary" icon="lock" disabled={mutation.isPending} onClick={confirm}>
            Unlock month
          </Button>
        </>
      }
    >
      <div className="pf-modal-body col gap12">
        <p className="t3 fs13">
          Unlocking lets postings dated in {period} land on this account again, until the month is
          finalized again. Items already reconciled stay reconciled. The reason is kept in the audit
          log with your name.
        </p>
        <div className="col gap6">
          <label className="pf-eyebrow" htmlFor={`${fieldId}-reason`}>
            Reason
          </label>
          <Input
            id={`${fieldId}-reason`}
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            onKeyDown={onKeyDown}
            placeholder="e.g. bank statement showed a fee the register was missing"
          />
        </div>
        <ApiErrorNotice error={error} />
      </div>
    </Modal>
  );
}
