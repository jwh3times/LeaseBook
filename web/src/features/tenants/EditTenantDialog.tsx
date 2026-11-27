import { useState } from 'react';
import { asApiError, type ApiError } from '@/api';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { Modal } from '@/components/Modal';
import { Button, Input, Select } from '@/design';
import { TENANT_LIFECYCLE_STATUSES, useUpdateTenant, type TenantDetail } from '@/lib/directory';
import { optionalText } from '@/lib/fieldConstraints';

/**
 * Corrects a tenant (#783): display name, contact email and phone, and lifecycle status.
 * `UpdateTenant` replaces all four, so each is prefilled from the read and sent back. Lifecycle is
 * operational; financial standing is derived from the ledger and is not set here.
 *
 * Nothing here moves money, so this is the form-shaped dialog: Enter in a field saves.
 */
export function EditTenantDialog({
  tenant,
  onClose,
}: {
  tenant: TenantDetail;
  onClose: () => void;
}) {
  const update = useUpdateTenant(tenant.id);
  const [displayName, setDisplayName] = useState(tenant.displayName);
  const [email, setEmail] = useState(tenant.contact.email ?? '');
  const [phone, setPhone] = useState(tenant.contact.phone ?? '');
  const [lifecycleStatus, setLifecycleStatus] = useState(tenant.lifecycleStatus);
  const [error, setError] = useState<ApiError | null>(null);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setError(null);
    try {
      await update.mutateAsync({
        id: tenant.id,
        displayName: displayName.trim(),
        contactEmail: optionalText(email),
        contactPhone: optionalText(phone),
        lifecycleStatus,
      });
      onClose();
    } catch (e) {
      setError(asApiError(e, 'Could not save the tenant. Check the fields and try again.'));
    }
  }

  return (
    <Modal
      title={`Edit ${tenant.displayName}`}
      onClose={onClose}
      onSubmit={submit}
      footer={
        <>
          <Button variant="ghost" size="sm" onClick={onClose}>
            Cancel
          </Button>
          <Button variant="primary" size="sm" type="submit" disabled={update.isPending}>
            {update.isPending ? 'Saving…' : 'Save tenant'}
          </Button>
        </>
      }
    >
      <div className="pf-modal-body">
        <div className="pf-formrow">
          <label htmlFor="te-name">Display name</label>
          <Input
            id="te-name"
            value={displayName}
            maxLength={200}
            onChange={(e) => setDisplayName(e.target.value)}
            required
          />
        </div>
        <div className="row gap12">
          <div className="pf-formrow grow">
            <label htmlFor="te-email">Email</label>
            <Input
              id="te-email"
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
            />
          </div>
          <div className="pf-formrow grow">
            <label htmlFor="te-phone">Phone</label>
            <Input
              id="te-phone"
              type="tel"
              value={phone}
              onChange={(e) => setPhone(e.target.value)}
            />
          </div>
        </div>
        <div className="pf-formrow">
          <label htmlFor="te-lifecycle">Lifecycle</label>
          <Select
            id="te-lifecycle"
            value={lifecycleStatus}
            onChange={(e) => setLifecycleStatus(e.target.value)}
          >
            {TENANT_LIFECYCLE_STATUSES.map((status) => (
              <option key={status} value={status}>
                {status.charAt(0).toUpperCase() + status.slice(1)}
              </option>
            ))}
          </Select>
        </div>
        {error && <ApiErrorNotice error={error} fallback="Could not save the tenant." />}
      </div>
    </Modal>
  );
}
