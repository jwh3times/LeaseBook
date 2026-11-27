import { useState } from 'react';
import { asApiError, type ApiError } from '@/api';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { Modal } from '@/components/Modal';
import { Button, Input } from '@/design';
import { useUpdateProperty, type PropertyDetail } from '@/lib/directory';
import { optionalText } from '@/lib/fieldConstraints';

/**
 * Corrects a property's address (#783). `UpdateProperty` replaces the whole property, so the address
 * fields are prefilled from the read and the management-fee override goes back exactly as the read
 * returned it. The override is not offered: nothing applies it today (#785). Ownership changes only
 * through Transfer ownership, which moves the held deposits with it.
 *
 * An address moves no money, so this is the form-shaped dialog: Enter in a field saves.
 */
export function EditPropertyDialog({
  property,
  onClose,
}: {
  property: PropertyDetail;
  onClose: () => void;
}) {
  const update = useUpdateProperty(property);
  const [address, setAddress] = useState(property.address);
  const [city, setCity] = useState(property.city ?? '');
  const [state, setState] = useState(property.state ?? '');
  const [zip, setZip] = useState(property.zip ?? '');
  const [error, setError] = useState<ApiError | null>(null);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setError(null);
    try {
      await update.mutateAsync({
        id: property.id,
        address: address.trim(),
        city: optionalText(city),
        state: optionalText(state),
        zip: optionalText(zip),
        mgmtFeeBps: property.mgmtFeeBps,
      });
      onClose();
    } catch (e) {
      setError(asApiError(e, 'Could not save the property. Check the fields and try again.'));
    }
  }

  return (
    <Modal
      title={`Edit ${property.address}`}
      onClose={onClose}
      onSubmit={submit}
      footer={
        <>
          <Button variant="ghost" size="sm" onClick={onClose}>
            Cancel
          </Button>
          <Button variant="primary" size="sm" type="submit" disabled={update.isPending}>
            {update.isPending ? 'Saving…' : 'Save property'}
          </Button>
        </>
      }
    >
      <div className="pf-modal-body">
        <div className="pf-formrow">
          <label htmlFor="pe-address">Address</label>
          <Input
            id="pe-address"
            value={address}
            maxLength={200}
            onChange={(e) => setAddress(e.target.value)}
            required
          />
        </div>
        <div className="row gap12">
          <div className="pf-formrow grow">
            <label htmlFor="pe-city">City</label>
            <Input
              id="pe-city"
              value={city}
              maxLength={100}
              onChange={(e) => setCity(e.target.value)}
            />
          </div>
          <div className="pf-formrow">
            <label htmlFor="pe-state">State</label>
            <Input
              id="pe-state"
              value={state}
              maxLength={50}
              onChange={(e) => setState(e.target.value)}
            />
          </div>
          <div className="pf-formrow">
            <label htmlFor="pe-zip">Zip</label>
            <Input
              id="pe-zip"
              value={zip}
              maxLength={20}
              onChange={(e) => setZip(e.target.value)}
            />
          </div>
        </div>
        {error && <ApiErrorNotice error={error} fallback="Could not save the property." />}
      </div>
    </Modal>
  );
}
