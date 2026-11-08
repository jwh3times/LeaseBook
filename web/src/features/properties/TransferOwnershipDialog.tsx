import { useId, useRef, useState } from 'react';
import { asApiError, type ApiError } from '@/api';
import { Button, Input, Select } from '@/design';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { ErrorAction } from '@/components/ErrorAction';
import { Modal } from '@/components/Modal';
import { reportFirstInvalidField } from '@/lib/fieldConstraints';
import { useOrgToday } from '@/lib/orgCalendar';
import {
  useOwners,
  useTransferPropertyOwnership,
  type PropertyDetail,
  type PropertyOwnershipTransferResult,
} from '@/lib/directory';
import { useSession } from '@/features/auth/useSession';
import { newSourceRef } from '@/features/tenants/ledgerMutations';

/**
 * A closed period or a locked deposit-bank month refuses the transfer. The server's own copy speaks
 * of posting into the open month, which a transfer has no field for, so the dialog names the date and
 * offers only what its reader can do (#554): unlocking is a PMAdmin action.
 */
function refusal(error: ApiError, isAdmin: boolean): ApiError {
  if (error.code === 'account_period_locked') {
    return {
      ...error,
      message: isAdmin
        ? 'A bank account holding these deposits is reconciled and locked for that month. Choose a later date, or unlock the month from its reconciliation history on Banking.'
        : 'A bank account holding these deposits is reconciled and locked for that month. Choose a later date, or ask an administrator to unlock it.',
    };
  }
  if (error.code === 'period_closed') {
    return {
      ...error,
      message: 'The accounting period for that date is closed. Choose a date in an open period.',
    };
  }
  return error;
}

export interface TransferredOwnership {
  ownerName: string;
  result: PropertyOwnershipTransferResult;
}

interface TransferOwnershipDialogProps {
  property: PropertyDetail;
  onClose: () => void;
  onTransferred: (transfer: TransferredOwnership) => void;
}

/**
 * Records a property sale (#555, ADR-036): the property passes to another owner on a date of today or
 * earlier, and the security deposits held for its tenants move to the buyer in the same entry. Seller
 * equity is not settled here, and the dialog says so before anything posts. A refusal — a backdated
 * date that would strand later deposit activity, a locked month — stays inline with the dialog open.
 *
 * It moves deposits, so it has no form and transfers only from its button, never from Enter, and the
 * button runs the fields' own constraints first (#711): the date's `max` refuses a date after the
 * organization's today in the field, before anything is sent.
 */
export function TransferOwnershipDialog({
  property,
  onClose,
  onTransferred,
}: TransferOwnershipDialogProps) {
  const fieldId = useId();
  const owners = useOwners();
  const transfer = useTransferPropertyOwnership(property.id);
  const isAdmin = useSession().data?.role === 'PMAdmin';
  // One key per dialog open, so a retried request records the transfer once.
  const sourceRef = useRef(newSourceRef());
  const bodyRef = useRef<HTMLDivElement>(null);
  const [toOwnerId, setToOwnerId] = useState('');
  // The latest date a transfer can carry is the organization's today, in its own zone, not the
  // viewer's (ADR-036, #642). null until the operator types a date: the field shows that today.
  const orgToday = useOrgToday();
  const today = orgToday.status === 'ready' ? orgToday.today : '';
  const [dateEdit, setEffectiveDate] = useState<string | null>(null);
  const effectiveDate = dateEdit ?? today;
  // A string is this dialog's own validation copy; an ApiError is the server's (ADR-025).
  const [error, setError] = useState<string | ApiError | null>(null);

  const candidates = (owners.data?.items ?? []).filter((o) => o.id !== property.ownerId);
  const ready = owners.isSuccess && candidates.length > 0 && orgToday.status === 'ready';

  async function confirm() {
    if (!ready || transfer.isPending) return;
    if (!reportFirstInvalidField(bodyRef.current)) return;
    if (!toOwnerId) {
      setError('Choose the new owner.');
      return;
    }
    if (!effectiveDate) {
      setError('Enter the date the transfer takes effect.');
      return;
    }
    setError(null);
    try {
      const result = await transfer.mutateAsync({
        toOwnerId,
        effectiveDate,
        sourceRef: sourceRef.current,
      });
      const ownerName = candidates.find((o) => o.id === toOwnerId)?.name ?? 'the new owner';
      onTransferred({ ownerName, result });
    } catch (e) {
      setError(refusal(asApiError(e, 'Could not transfer the property.'), isAdmin));
    }
  }

  return (
    <Modal
      title={`Transfer ${property.address}`}
      onClose={onClose}
      footer={
        <>
          <Button variant="ghost" size="sm" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="primary"
            size="sm"
            icon="owners"
            disabled={!ready || transfer.isPending}
            onClick={() => void confirm()}
          >
            Transfer ownership
          </Button>
        </>
      }
    >
      <div ref={bodyRef} className="pf-modal-body col gap12">
        <p className="t3 fs13">
          {property.address} passes from {property.ownerName} to the new owner on the effective
          date. Security deposits held for this property's tenants move to the new owner on that
          date. The transfer does not settle the current owner's equity; that is a separate step.
        </p>
        <div className="pf-formrow">
          <label htmlFor={`${fieldId}-owner`}>New owner</label>
          {owners.isPending ? (
            <div role="status" aria-label="Loading owners">
              <div className="pf-skeleton" style={{ height: 32 }} />
            </div>
          ) : owners.isError ? (
            <div className="col gap6">
              <ApiErrorNotice
                error={owners.error}
                fallback="Couldn’t load the owner list."
                kind="read"
              />
              <ErrorAction
                error={owners.error}
                onRetry={() => void owners.refetch()}
                retrying={owners.isFetching}
              />
            </div>
          ) : candidates.length === 0 ? (
            <p className="t3 fs12">
              No other owner to transfer to — add the buyer as an owner first.
            </p>
          ) : (
            <Select
              id={`${fieldId}-owner`}
              value={toOwnerId}
              onChange={(e) => setToOwnerId(e.target.value)}
            >
              <option value="">Select the new owner…</option>
              {candidates.map((o) => (
                <option key={o.id} value={o.id}>
                  {o.name}
                </option>
              ))}
            </Select>
          )}
        </div>
        <div className="pf-formrow">
          <label htmlFor={`${fieldId}-date`}>Effective date</label>
          <Input
            id={`${fieldId}-date`}
            type="date"
            value={effectiveDate}
            max={today || undefined}
            onChange={(e) => setEffectiveDate(e.target.value)}
          />
          {orgToday.status === 'error' && (
            <div className="col gap6">
              <ApiErrorNotice
                error={orgToday.error}
                fallback="Couldn’t load the organization’s settings, so today’s date isn’t known."
                kind="read"
              />
              <ErrorAction
                error={orgToday.error}
                onRetry={orgToday.retry}
                retrying={orgToday.retrying}
              />
            </div>
          )}
        </div>
        {error &&
          (typeof error === 'string' ? (
            <div className="err" role="alert">
              {error}
            </div>
          ) : (
            <ApiErrorNotice error={error} fallback="Could not transfer the property." />
          ))}
      </div>
    </Modal>
  );
}
