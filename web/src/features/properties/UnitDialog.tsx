import { useState } from 'react';
import { asApiError, type ApiError } from '@/api';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { Modal } from '@/components/Modal';
import { Button, Input, Select } from '@/design';
import {
  num,
  useCreateUnit,
  useUpdateUnit,
  type PropertyDetail,
  type UnitRow,
} from '@/lib/directory';

/**
 * Adds a unit to a property, or edits one (#780). The three fields are all a unit carries: its label,
 * its asking rent — what it is offered at, which charges nothing; the lease rent is what is
 * charged — and its availability. A unit never moves to another property, and none is deleted:
 * unavailable is how one is retired.
 */
export function UnitDialog({
  property,
  unit,
  onClose,
}: {
  property: PropertyDetail;
  /** The unit to edit; absent to add one. */
  unit?: UnitRow;
  onClose: () => void;
}) {
  const create = useCreateUnit(property);
  const update = useUpdateUnit(property);
  const saving = create.isPending || update.isPending;
  const [label, setLabel] = useState(unit?.label ?? '');
  const [rent, setRent] = useState(unit ? String(num(unit.rent)) : '');
  const [availability, setAvailability] = useState(unit?.availability ?? 'available');
  const [error, setError] = useState<ApiError | null>(null);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setError(null);
    const fields = { label: label.trim(), rent: Number(rent), availability };
    try {
      if (unit) await update.mutateAsync({ id: unit.id, ...fields });
      else await create.mutateAsync({ propertyId: property.id, ...fields });
      onClose();
    } catch (e) {
      setError(asApiError(e, 'Could not save the unit. Check the fields and try again.'));
    }
  }

  return (
    <Modal
      title={unit ? `Edit unit ${unit.label}` : `Add a unit to ${property.address}`}
      onClose={onClose}
      onSubmit={submit}
      footer={
        <>
          <Button variant="ghost" size="sm" onClick={onClose}>
            Cancel
          </Button>
          <Button variant="primary" size="sm" type="submit" disabled={saving}>
            {saving ? 'Saving…' : unit ? 'Save unit' : 'Add unit'}
          </Button>
        </>
      }
    >
      <div className="pf-modal-body">
        <div className="pf-formrow">
          <label htmlFor="u-label">Label</label>
          <Input
            id="u-label"
            value={label}
            maxLength={60}
            onChange={(e) => setLabel(e.target.value)}
            required
          />
        </div>
        <div className="row gap12">
          <div className="pf-formrow grow">
            <label htmlFor="u-rent">Asking rent</label>
            <Input
              id="u-rent"
              type="number"
              min="0"
              step="0.01"
              value={rent}
              onChange={(e) => setRent(e.target.value)}
              required
            />
          </div>
          <div className="pf-formrow grow">
            <label htmlFor="u-availability">Availability</label>
            <Select
              id="u-availability"
              value={availability}
              onChange={(e) => setAvailability(e.target.value)}
            >
              <option value="available">Available</option>
              <option value="unavailable">Unavailable</option>
            </Select>
          </div>
        </div>
        {error && <ApiErrorNotice error={error} fallback="Could not save the unit." />}
      </div>
    </Modal>
  );
}
