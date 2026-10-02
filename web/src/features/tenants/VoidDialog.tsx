import { useMutation } from '@tanstack/react-query';
import { useId, useRef, useState, type KeyboardEvent } from 'react';
import { Link } from 'react-router';
import { Button, Icon, Input } from '@/design';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { AudienceHint } from '@/components/InternalNote';
import { Modal } from '@/components/Modal';
import {
  type LedgerPostError,
  LOCKED_PERIOD_MESSAGE,
  newSourceRef,
  type PostResult,
  voidDescription,
  voidEntry,
} from './ledgerMutations';

interface VoidDialogProps {
  entryId: string;
  /**
   * The voided entry's owner-facing description, as the ledger shows it. The reversal's description
   * is derived from it server-side, so the dialog can say exactly what the owner statement will read.
   */
  description?: string | null;
  onClose: () => void;
  onVoided: (reversalEntryId: string) => void;
}

/**
 * Void/reverse confirmation (§C.4). A reason is required; on confirm it posts a linked reversal through
 * the WP-01 void command (P54 idempotency key). An already-reversed entry surfaces a friendly message
 * rather than an error; a deduped double-submit is treated as already voided.
 *
 * The reason is staff-only (#468, ADR-047): it becomes the reversal's internal note, and the owner
 * statement shows `Void — {description}` — that a correction happened, and to what, but not why.
 */
export function VoidDialog({ entryId, description, onClose, onVoided }: VoidDialogProps) {
  const fieldId = useId();
  const [reason, setReason] = useState('');
  const [error, setError] = useState<LedgerPostError | null>(null);
  const sourceRef = useRef(newSourceRef());
  const [refundCheckRequired, setRefundCheckRequired] = useState(false);
  const refundCheckNumber = /Refund check #(\d+)/.exec(description ?? '')?.[1];

  const mutation = useMutation<PostResult, LedgerPostError>({
    mutationFn: () => voidEntry(entryId, reason.trim(), sourceRef.current),
    onSuccess: (result) => onVoided(result.entryId),
    onError: (err) => {
      if (err.code === 'refund_check_void_required') {
        // #473: a refund check is voided from its check record, which keeps the number and the bank
        // clearance in step with the reversal. The generic void refuses it, so say where to go.
        setError(null);
        setRefundCheckRequired(true);
      } else if (err.code === 'already_reversed') {
        setError({ ...err, message: 'This entry has already been voided.' });
      } else if (err.code === 'duplicate_source_ref' && err.existingEntryId) {
        onVoided(err.existingEntryId);
      } else if (err.code === 'account_period_locked') {
        // Reversal lands a bank line in the original's month; if it's reconciled, the lock blocks it.
        setError({ ...err, message: LOCKED_PERIOD_MESSAGE });
      } else {
        setError(err);
      }
    },
  });

  const confirm = () => {
    if (reason.trim() === '') {
      setError({ message: 'A reason is required to void an entry.' });
      return;
    }
    setError(null);
    setRefundCheckRequired(false);
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
      title="Void entry"
      onClose={onClose}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button variant="primary" icon="x" disabled={mutation.isPending} onClick={confirm}>
            Void entry
          </Button>
        </>
      }
    >
      <div className="pf-modal-body col gap12">
        <p className="t3 fs13">
          Voiding posts a linked reversal — the original stays in the ledger, struck through. The
          owner statement will show{' '}
          <b style={{ color: 'var(--text)' }}>“{voidDescription(description)}”</b>; your reason is
          kept as a staff-only internal note and never appears on it.
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
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            onKeyDown={onKeyDown}
            placeholder="e.g. entered in error"
          />
        </div>
        <ApiErrorNotice error={error} />
        {refundCheckRequired && (
          <div className="pf-api-error col gap6" role="alert">
            <span>
              <Icon name="alert" size={14} />{' '}
              {refundCheckNumber
                ? `This entry is refund check #${refundCheckNumber}.`
                : 'This entry is a refund check.'}{' '}
              Void it from the refund check list on Banking, which keeps the check number and its
              bank clearance in step with the reversal.
            </span>
            <Link to="/banking" className="fw6">
              Open refund checks on Banking
            </Link>
          </div>
        )}
      </div>
    </Modal>
  );
}
