import { useRef, useState } from 'react';
import { asApiError, type ApiError } from '@/api';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { Modal } from '@/components/Modal';
import { Button, Input } from '@/design';
import { num, useUpdateOwner, type OwnerDetail } from '@/lib/directory';
import { optionalText, reportFirstInvalidField } from '@/lib/fieldConstraints';

/** Basis points as the percent the field shows: 833 is "8.33", and no rate is an empty field. */
function percentOf(bps: number | string | null): string {
  return bps == null ? '' : String(num(bps) / 100);
}

/** The field's percent back to basis points, exactly: "8.33" is 833, and an empty field is no rate. */
function bpsOf(percent: string): number | null {
  return percent.trim() === '' ? null : Math.round(Number(percent) * 100);
}

/**
 * Corrects an owner (#782). `UpdateOwner` replaces the whole owner, so every field is prefilled from
 * the read and sent back, initials included: a field left alone is saved as it was.
 *
 * The default management-fee rate and the reserve are money policy. They overwrite in place, with no
 * effective date, and the next fee and disbursement run uses them; the audit trail keeps what they
 * were before. The dialog says so, because a run already made is not recalculated.
 *
 * Because it sets money policy, it follows the money dialogs' rule (#711): no form, and it saves only
 * from its button, never from Enter in a field. The button runs the fields' own constraints first.
 */
export function EditOwnerDialog({ owner, onClose }: { owner: OwnerDetail; onClose: () => void }) {
  const update = useUpdateOwner(owner);
  const [name, setName] = useState(owner.name);
  const [initials, setInitials] = useState(owner.initials ?? '');
  const [email, setEmail] = useState(owner.contact.email ?? '');
  const [phone, setPhone] = useState(owner.contact.phone ?? '');
  const [feePercent, setFeePercent] = useState(percentOf(owner.defaultMgmtFeeBps));
  const [reserve, setReserve] = useState(String(num(owner.reserveAmount)));
  const [error, setError] = useState<ApiError | null>(null);
  const bodyRef = useRef<HTMLDivElement>(null);

  async function save() {
    if (update.isPending) return;
    if (!reportFirstInvalidField(bodyRef.current)) return;
    setError(null);
    try {
      await update.mutateAsync({
        id: owner.id,
        name: name.trim(),
        initials: optionalText(initials),
        contactEmail: optionalText(email),
        contactPhone: optionalText(phone),
        defaultMgmtFeeBps: bpsOf(feePercent),
        reserveAmount: Number(reserve),
      });
      onClose();
    } catch (e) {
      setError(asApiError(e, 'Could not save the owner. Check the fields and try again.'));
    }
  }

  return (
    <Modal
      title={`Edit ${owner.name}`}
      onClose={onClose}
      footer={
        <>
          <Button variant="ghost" size="sm" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="primary"
            size="sm"
            onClick={() => void save()}
            disabled={update.isPending}
          >
            {update.isPending ? 'Saving…' : 'Save owner'}
          </Button>
        </>
      }
    >
      <div ref={bodyRef} className="pf-modal-body">
        <div className="row gap12">
          <div className="pf-formrow grow">
            <label htmlFor="oe-name">Name</label>
            <Input
              id="oe-name"
              value={name}
              maxLength={200}
              onChange={(e) => setName(e.target.value)}
              required
            />
          </div>
          <div className="pf-formrow">
            <label htmlFor="oe-initials">Initials</label>
            <Input
              id="oe-initials"
              value={initials}
              maxLength={8}
              onChange={(e) => setInitials(e.target.value.toUpperCase())}
            />
          </div>
        </div>
        <div className="row gap12">
          <div className="pf-formrow grow">
            <label htmlFor="oe-email">Email</label>
            <Input
              id="oe-email"
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
            />
          </div>
          <div className="pf-formrow grow">
            <label htmlFor="oe-phone">Phone</label>
            <Input
              id="oe-phone"
              type="tel"
              value={phone}
              onChange={(e) => setPhone(e.target.value)}
            />
          </div>
        </div>
        <div className="row gap12">
          <div className="pf-formrow grow">
            <label htmlFor="oe-fee">Mgmt fee %</label>
            <Input
              id="oe-fee"
              type="number"
              // Basis points: a rate is stored to the hundredth of a percent, so 8.25% is exact.
              step="0.01"
              min="0"
              max="100"
              value={feePercent}
              onChange={(e) => setFeePercent(e.target.value)}
              aria-describedby="oe-fee-hint"
            />
            <span id="oe-fee-hint" className="t3 fs12">
              Leave blank for no default rate.
            </span>
          </div>
          <div className="pf-formrow grow">
            <label htmlFor="oe-reserve">Reserve floor</label>
            <Input
              id="oe-reserve"
              type="number"
              step="0.01"
              min="0"
              value={reserve}
              onChange={(e) => setReserve(e.target.value)}
              required
            />
          </div>
        </div>
        <p className="t3 fs12">
          A fee-rate or reserve change applies from the next run. Runs already made keep the rate
          and reserve they used, and the audit log records what these were before.
        </p>
        {error && <ApiErrorNotice error={error} fallback="Could not save the owner." />}
      </div>
    </Modal>
  );
}
