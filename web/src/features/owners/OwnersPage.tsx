import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Avatar, Button, Input, Money, type TableColumn } from '@/design';
import { IndexView } from '@/lib/IndexView';
import { Modal } from '@/lib/Modal';
import { num, useCreateOwner, useOwners, type OwnerListRow } from '@/lib/directory';

function ownerMatches(row: OwnerListRow, q: string): boolean {
  return row.name.toLowerCase().includes(q);
}

const columns: TableColumn<OwnerListRow>[] = [
  {
    key: 'name',
    header: 'Owner',
    render: (r) => (
      <span className="row gap10">
        <Avatar initials={r.initials ?? r.name.slice(0, 2).toUpperCase()} size={28} />
        <span className="strong">{r.name}</span>
      </span>
    ),
  },
  { key: 'units', header: 'Units', num: true, render: (r) => r.units },
  { key: 'properties', header: 'Properties', num: true, render: (r) => r.properties },
  { key: 'operating', header: 'Operating', num: true, render: (r) => <Money value={num(r.operating)} colorize /> },
  { key: 'deposits', header: 'Deposits', num: true, render: (r) => <Money value={num(r.deposits)} /> },
  { key: 'total', header: 'Total', num: true, render: (r) => <Money value={num(r.total)} /> },
];

export function OwnersPage() {
  const navigate = useNavigate();
  const query = useOwners();
  const [showNew, setShowNew] = useState(false);

  return (
    <>
      <IndexView
        kind="owners"
        title="Owners"
        count={query.data?.total}
        query={query}
        columns={columns}
        rowKey={(r) => r.id}
        matches={ownerMatches}
        onOpen={(r) => navigate(`/owners/${r.id}`)}
        onNew={() => setShowNew(true)}
        newLabel="New owner"
        searchPlaceholder="Filter owners…"
        emptyTitle="No owners yet"
        emptyIcon="owners"
      />
      {showNew && <NewOwnerModal onClose={() => setShowNew(false)} onCreated={(id) => navigate(`/owners/${id}`)} />}
    </>
  );
}

function NewOwnerModal({ onClose, onCreated }: { onClose: () => void; onCreated: (id: string) => void }) {
  const create = useCreateOwner();
  const [name, setName] = useState('');
  const [initials, setInitials] = useState('');
  const [email, setEmail] = useState('');
  const [feePct, setFeePct] = useState('8');
  const [error, setError] = useState<string | null>(null);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setError(null);
    const bps = Math.round(Number(feePct) * 100);
    try {
      const result = await create.mutateAsync({
        name,
        initials: initials || null,
        contactEmail: email || null,
        contactPhone: null,
        defaultMgmtFeeBps: Number.isFinite(bps) ? bps : null,
        reserveAmount: 0,
      });
      onCreated(result.id);
    } catch {
      setError('Could not create the owner. Check the fields and try again.');
    }
  }

  return (
    <Modal
      title="New owner"
      onClose={onClose}
      footer={
        <>
          <Button variant="ghost" size="sm" onClick={onClose}>Cancel</Button>
          <Button variant="primary" size="sm" onClick={submit} disabled={create.isPending || !name}>
            {create.isPending ? 'Creating…' : 'Create owner'}
          </Button>
        </>
      }
    >
      <form className="pf-modal-body" onSubmit={submit}>
        <div className="pf-formrow">
          <label htmlFor="o-name">Name</label>
          <Input id="o-name" value={name} onChange={(e) => setName(e.target.value)} required />
        </div>
        <div className="row gap12">
          <div className="pf-formrow grow">
            <label htmlFor="o-initials">Initials</label>
            <Input id="o-initials" value={initials} maxLength={4} onChange={(e) => setInitials(e.target.value.toUpperCase())} />
          </div>
          <div className="pf-formrow grow">
            <label htmlFor="o-fee">Mgmt fee %</label>
            <Input id="o-fee" type="number" step="0.1" min="0" max="100" value={feePct} onChange={(e) => setFeePct(e.target.value)} />
          </div>
        </div>
        <div className="pf-formrow">
          <label htmlFor="o-email">Email</label>
          <Input id="o-email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} />
        </div>
        {error && <div className="err" role="alert">{error}</div>}
      </form>
    </Modal>
  );
}
