import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Badge, Button, Input, Select, type TableColumn } from '@/design';
import { IndexView } from '@/lib/IndexView';
import { Modal } from '@/lib/Modal';
import {
  num,
  useCreateProperty,
  useOwners,
  useProperties,
  type PropertyListRow,
} from '@/lib/directory';

function propertyMatches(row: PropertyListRow, q: string): boolean {
  return row.address.toLowerCase().includes(q) || row.ownerName.toLowerCase().includes(q);
}

const columns: TableColumn<PropertyListRow>[] = [
  { key: 'address', header: 'Address', render: (r) => <span className="strong">{r.address}</span> },
  { key: 'city', header: 'City', render: (r) => r.city ?? '—' },
  { key: 'owner', header: 'Owner', render: (r) => r.ownerName },
  { key: 'units', header: 'Units', num: true, render: (r) => r.units },
  {
    key: 'occ',
    header: 'Occupancy',
    render: (r) => (
      <Badge tone={num(r.units) > 0 && num(r.occupied) === num(r.units) ? 'pos' : 'neutral'} dot>
        {r.occupied}/{r.units} occupied
      </Badge>
    ),
  },
];

export function PropertiesPage() {
  const navigate = useNavigate();
  const query = useProperties();
  const [showNew, setShowNew] = useState(false);

  return (
    <>
      <IndexView
        kind="properties"
        title="Properties"
        count={query.data?.total}
        query={query}
        columns={columns}
        rowKey={(r) => r.id}
        matches={propertyMatches}
        onOpen={(r) => navigate(`/properties/${r.id}`)}
        onNew={() => setShowNew(true)}
        newLabel="New property"
        searchPlaceholder="Filter properties…"
        emptyTitle="No properties yet"
        emptyIcon="building"
      />
      {showNew && (
        <NewPropertyModal
          onClose={() => setShowNew(false)}
          onCreated={(id) => navigate(`/properties/${id}`)}
        />
      )}
    </>
  );
}

function NewPropertyModal({
  onClose,
  onCreated,
}: {
  onClose: () => void;
  onCreated: (id: string) => void;
}) {
  const create = useCreateProperty();
  const owners = useOwners();
  const [ownerId, setOwnerId] = useState('');
  const [address, setAddress] = useState('');
  const [city, setCity] = useState('');
  const [state, setState] = useState('NC');
  const [error, setError] = useState<string | null>(null);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setError(null);
    if (!ownerId) {
      setError('Choose an owner for this property.');
      return;
    }
    try {
      const result = await create.mutateAsync({
        ownerId,
        address,
        city: city || null,
        state: state || null,
        zip: null,
        mgmtFeeBps: null,
      });
      onCreated(result.id);
    } catch {
      setError('Could not create the property. Check the fields and try again.');
    }
  }

  return (
    <Modal
      title="New property"
      onClose={onClose}
      footer={
        <>
          <Button variant="ghost" size="sm" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="primary"
            size="sm"
            onClick={submit}
            disabled={create.isPending || !address || !ownerId}
          >
            {create.isPending ? 'Creating…' : 'Create property'}
          </Button>
        </>
      }
    >
      <form className="pf-modal-body" onSubmit={submit}>
        <div className="pf-formrow">
          <label htmlFor="p-owner">Owner</label>
          <Select
            id="p-owner"
            value={ownerId}
            onChange={(e) => setOwnerId(e.target.value)}
            required
          >
            <option value="">Select an owner…</option>
            {(owners.data?.items ?? []).map((o) => (
              <option key={o.id} value={o.id}>
                {o.name}
              </option>
            ))}
          </Select>
        </div>
        <div className="pf-formrow">
          <label htmlFor="p-address">Address</label>
          <Input
            id="p-address"
            value={address}
            onChange={(e) => setAddress(e.target.value)}
            required
          />
        </div>
        <div className="row gap12">
          <div className="pf-formrow grow">
            <label htmlFor="p-city">City</label>
            <Input id="p-city" value={city} onChange={(e) => setCity(e.target.value)} />
          </div>
          <div className="pf-formrow" style={{ width: 90 }}>
            <label htmlFor="p-state">State</label>
            <Input
              id="p-state"
              value={state}
              maxLength={2}
              onChange={(e) => setState(e.target.value.toUpperCase())}
            />
          </div>
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
