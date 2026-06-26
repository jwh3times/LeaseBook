import { useMutation } from '@tanstack/react-query';
import { useRef, useState, type KeyboardEvent } from 'react';
import { Button, Input } from '@/design';
import { Modal } from '@/components/Modal';
import {
  type LedgerPostError,
  LOCKED_PERIOD_MESSAGE,
  newSourceRef,
  type PostResult,
  voidEntry,
} from './ledgerMutations';

interface VoidDialogProps {
  entryId: string;
  onClose: () => void;
  onVoided: (reversalEntryId: string) => void;
}

/**
 * Void/reverse confirmation (§C.4). A reason is required; on confirm it posts a linked reversal through
 * the WP-01 void command (P54 idempotency key). An already-reversed entry surfaces a friendly message
 * rather than an error; a deduped double-submit is treated as already voided.
 */
export function VoidDialog({ entryId, onClose, onVoided }: VoidDialogProps) {
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const sourceRef = useRef(newSourceRef());

  const mutation = useMutation<PostResult, LedgerPostError>({
    mutationFn: () => voidEntry(entryId, reason.trim(), sourceRef.current),
    onSuccess: (result) => onVoided(result.entryId),
    onError: (err) => {
      if (err.code === 'already_reversed') {
        setError('This entry has already been voided.');
      } else if (err.code === 'duplicate_source_ref' && err.existingEntryId) {
        onVoided(err.existingEntryId);
      } else if (err.code === 'account_period_locked') {
        // Reversal lands a bank line in the original's month; if it's reconciled, the lock blocks it.
        setError(LOCKED_PERIOD_MESSAGE);
      } else {
        setError(err.message);
      }
    },
  });

  const confirm = () => {
    if (reason.trim() === '') {
      setError('A reason is required to void an entry.');
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
          Voiding posts a linked reversal — the original stays in the ledger, struck through, with
          this reason recorded in its history.
        </p>
        <label className="col gap6">
          <span className="pf-eyebrow">Reason</span>
          <Input
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            onKeyDown={onKeyDown}
            placeholder="e.g. entered in error"
            aria-label="Reason"
          />
        </label>
        {error && (
          <span className="pf-composer-error" role="alert">
            {error}
          </span>
        )}
      </div>
    </Modal>
  );
}
