import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Button, Input, Money, Select, type TableColumn } from '@/design';
import { IndexView } from '@/lib/IndexView';
import { Modal } from '@/lib/Modal';
import { TenantStatusBadge } from '@/lib/StatusBadge';
import { num, useCreateTenant, useTenants, type TenantListRow } from '@/lib/directory';

const TENANT_STATUSES = ['current', 'late', 'prepaid', 'evicting', 'past'] as const;

function tenantMatches(row: TenantListRow, q: string): boolean {
  return (
    row.displayName.toLowerCase().includes(q) || (row.unitLabel?.toLowerCase().includes(q) ?? false)
  );
}

const columns: TableColumn<TenantListRow>[] = [
  { key: 'name', header: 'Tenant', render: (r) => <span className="strong">{r.displayName}</span> },
  { key: 'unit', header: 'Unit', render: (r) => r.unitLabel ?? <span className="muted">—</span> },
  { key: 'rent', header: 'Rent', num: true, render: (r) => <Money value={num(r.rent)} /> },
  {
    key: 'balance',
    header: 'Balance',
    num: true,
    render: (r) => <Money value={num(r.balance)} colorize />,
  },
  { key: 'status', header: 'Status', render: (r) => <TenantStatusBadge status={r.status} /> },
];

export function TenantsPage() {
  const navigate = useNavigate();
  const query = useTenants();
  const [showNew, setShowNew] = useState(false);

  return (
    <>
      <IndexView
        kind="tenants"
        title="Tenants"
        count={query.data?.total}
        query={query}
        columns={columns}
        rowKey={(r) => r.id}
        matches={tenantMatches}
        onOpen={(r) => navigate(`/tenants/${r.id}`)}
        onNew={() => setShowNew(true)}
        newLabel="New tenant"
        searchPlaceholder="Filter tenants…"
        emptyTitle="No tenants yet"
        emptyIcon="tenants"
      />
      {showNew && (
        <NewTenantModal
          onClose={() => setShowNew(false)}
          onCreated={(id) => navigate(`/tenants/${id}`)}
        />
      )}
    </>
  );
}

function NewTenantModal({
  onClose,
  onCreated,
}: {
  onClose: () => void;
  onCreated: (id: string) => void;
}) {
  const create = useCreateTenant();
  const [displayName, setDisplayName] = useState('');
  const [contactEmail, setContactEmail] = useState('');
  const [contactPhone, setContactPhone] = useState('');
  const [status, setStatus] = useState<string>('current');
  const [error, setError] = useState<string | null>(null);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setError(null);
    try {
      const result = await create.mutateAsync({
        displayName,
        contactEmail: contactEmail || null,
        contactPhone: contactPhone || null,
        status,
      });
      onCreated(result.id);
    } catch {
      setError('Could not create the tenant. Check the fields and try again.');
    }
  }

  return (
    <Modal
      title="New tenant"
      onClose={onClose}
      footer={
        <>
          <Button variant="ghost" size="sm" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="primary"
            size="sm"
            form="new-tenant"
            onClick={submit}
            disabled={create.isPending || !displayName}
          >
            {create.isPending ? 'Creating…' : 'Create tenant'}
          </Button>
        </>
      }
    >
      <form id="new-tenant" className="pf-modal-body" onSubmit={submit}>
        <div className="pf-formrow">
          <label htmlFor="t-name">Display name</label>
          <Input
            id="t-name"
            value={displayName}
            onChange={(e) => setDisplayName(e.target.value)}
            required
          />
        </div>
        <div className="pf-formrow">
          <label htmlFor="t-email">Email</label>
          <Input
            id="t-email"
            type="email"
            value={contactEmail}
            onChange={(e) => setContactEmail(e.target.value)}
          />
        </div>
        <div className="pf-formrow">
          <label htmlFor="t-phone">Phone</label>
          <Input
            id="t-phone"
            value={contactPhone}
            onChange={(e) => setContactPhone(e.target.value)}
          />
        </div>
        <div className="pf-formrow">
          <label htmlFor="t-status">Status</label>
          <Select id="t-status" value={status} onChange={(e) => setStatus(e.target.value)}>
            {TENANT_STATUSES.map((s) => (
              <option key={s} value={s}>
                {s.charAt(0).toUpperCase() + s.slice(1)}
              </option>
            ))}
          </Select>
        </div>
        {error && (
          <div className="err" role="alert">
            {error}
          </div>
        )}
      </form>
    </Modal>
  );
}
