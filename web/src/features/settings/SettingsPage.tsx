import { useEffect, useState } from 'react';
import { Badge, Button, Card, CardHeader, Input, Select, Table, type TableColumn } from '@/design';
import { Modal } from '@/lib/Modal';
import {
  useBankAccounts,
  useCreateBankAccount,
  useOrgSettings,
  useUpdateOrgSettings,
  type BankAccount,
  type OrgSettings,
} from '@/lib/settings';

const BANK_PURPOSES = ['trust', 'deposit', 'operating'] as const;

export function SettingsPage() {
  const settings = useOrgSettings();

  return (
    <div className="pf-fade">
      <div className="pf-pagehd">
        <div>
          <h2>Settings</h2>
        </div>
      </div>
      {settings.isPending ? (
        <Card pad>
          <div className="pf-skeleton" style={{ maxWidth: 280, height: 22 }} />
        </Card>
      ) : settings.isError || !settings.data ? (
        <Card pad>Couldn’t load settings.</Card>
      ) : (
        <div className="col gap16">
          <OrgProfileForm initial={settings.data} />
          <BankAccountsSection />
          <Card pad>
            <p className="pf-section-title">Management fees</p>
            <p className="t3 fs13">
              Default fee rates are set per owner (Owners → owner → edit), with an optional
              per-property override on the property. Stored as basis points; fee computation arrives
              in a later milestone.
            </p>
          </Card>
        </div>
      )}
    </div>
  );
}

function OrgProfileForm({ initial }: { initial: OrgSettings }) {
  const update = useUpdateOrgSettings();
  const [form, setForm] = useState(initial);
  const [saved, setSaved] = useState(false);

  useEffect(() => setForm(initial), [initial]);

  function set<K extends keyof OrgSettings>(key: K, value: OrgSettings[K]) {
    setForm((current) => ({ ...current, [key]: value }));
    setSaved(false);
  }

  async function save(event: React.FormEvent) {
    event.preventDefault();
    await update.mutateAsync({
      accountingBasis: form.accountingBasis,
      moneyNegativeDisplay: form.moneyNegativeDisplay,
      legalName: form.legalName ?? null,
      address: form.address ?? null,
      city: form.city ?? null,
      state: form.state ?? null,
      zip: form.zip ?? null,
      phone: form.phone ?? null,
      logoBlobRef: form.logoBlobRef ?? null,
    });
    setSaved(true);
  }

  return (
    <Card>
      <CardHeader title="Organization" sub="Profile, accounting basis and money display." />
      <form className="pf-pad col gap14" onSubmit={save}>
        <div className="pf-formrow">
          <label htmlFor="s-legal">Legal name</label>
          <Input
            id="s-legal"
            value={form.legalName ?? ''}
            onChange={(e) => set('legalName', e.target.value)}
          />
        </div>
        <div className="pf-formrow">
          <label htmlFor="s-addr">Address</label>
          <Input
            id="s-addr"
            value={form.address ?? ''}
            onChange={(e) => set('address', e.target.value)}
          />
        </div>
        <div className="row gap12 wrap">
          <div className="pf-formrow grow">
            <label htmlFor="s-city">City</label>
            <Input
              id="s-city"
              value={form.city ?? ''}
              onChange={(e) => set('city', e.target.value)}
            />
          </div>
          <div className="pf-formrow" style={{ width: 90 }}>
            <label htmlFor="s-state">State</label>
            <Input
              id="s-state"
              value={form.state ?? ''}
              maxLength={2}
              onChange={(e) => set('state', e.target.value.toUpperCase())}
            />
          </div>
          <div className="pf-formrow" style={{ width: 120 }}>
            <label htmlFor="s-zip">ZIP</label>
            <Input id="s-zip" value={form.zip ?? ''} onChange={(e) => set('zip', e.target.value)} />
          </div>
        </div>
        <div className="pf-formrow">
          <label htmlFor="s-phone">Phone</label>
          <Input
            id="s-phone"
            value={form.phone ?? ''}
            onChange={(e) => set('phone', e.target.value)}
          />
        </div>

        <div className="row gap12 wrap">
          <div className="pf-formrow grow">
            <label htmlFor="s-basis">Accounting basis</label>
            <Select
              id="s-basis"
              value={form.accountingBasis}
              onChange={(e) => set('accountingBasis', e.target.value)}
            >
              <option value="cash">Cash</option>
              <option value="accrual">Accrual</option>
            </Select>
          </div>
          <div className="pf-formrow grow">
            <label htmlFor="s-neg">Negative amounts</label>
            <Select
              id="s-neg"
              value={form.moneyNegativeDisplay}
              onChange={(e) => set('moneyNegativeDisplay', e.target.value)}
            >
              <option value="minus">Minus sign (-1,250.00)</option>
              <option value="parens">Parentheses (1,250.00)</option>
            </Select>
          </div>
        </div>

        <div className="row gap12">
          <Button variant="primary" size="sm" onClick={save} disabled={update.isPending}>
            {update.isPending ? 'Saving…' : 'Save changes'}
          </Button>
          {saved && (
            <Badge tone="pos" dot>
              Saved
            </Badge>
          )}
          {update.isError && <span className="err">Couldn’t save. You may need admin rights.</span>}
        </div>
      </form>
    </Card>
  );
}

const bankColumns: TableColumn<BankAccount>[] = [
  { key: 'name', header: 'Account', render: (b) => <span className="strong">{b.name}</span> },
  { key: 'institution', header: 'Institution', render: (b) => b.institution ?? '—' },
  { key: 'mask', header: 'Mask', render: (b) => (b.mask ? `••${b.mask}` : '—') },
  {
    key: 'purpose',
    header: 'Purpose',
    render: (b) => (
      <Badge tone={b.purpose === 'operating' ? 'neutral' : 'accent'} dot>
        {b.purpose}
      </Badge>
    ),
  },
];

function BankAccountsSection() {
  const banks = useBankAccounts();
  const [showNew, setShowNew] = useState(false);

  return (
    <Card>
      <CardHeader
        title="Trust bank accounts"
        sub="Creating an account provisions its ledger account."
        actions={
          <Button variant="primary" size="sm" icon="plus" onClick={() => setShowNew(true)}>
            New account
          </Button>
        }
      />
      {banks.isPending ? (
        <div className="pf-pad">
          <div className="pf-skeleton" />
        </div>
      ) : (banks.data?.length ?? 0) === 0 ? (
        <div className="pf-pad t3 fs13">No bank accounts yet.</div>
      ) : (
        <Table columns={bankColumns} rows={banks.data ?? []} rowKey={(b) => b.id} />
      )}
      {showNew && <NewBankModal onClose={() => setShowNew(false)} />}
    </Card>
  );
}

function NewBankModal({ onClose }: { onClose: () => void }) {
  const create = useCreateBankAccount();
  const [name, setName] = useState('');
  const [institution, setInstitution] = useState('');
  const [mask, setMask] = useState('');
  const [purpose, setPurpose] = useState<string>('trust');
  const [error, setError] = useState<string | null>(null);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setError(null);
    try {
      await create.mutateAsync({
        name,
        institution: institution || null,
        mask: mask || null,
        purpose,
      });
      onClose();
    } catch {
      setError('Could not create the account. Check the fields and try again.');
    }
  }

  return (
    <Modal
      title="New bank account"
      onClose={onClose}
      footer={
        <>
          <Button variant="ghost" size="sm" onClick={onClose}>
            Cancel
          </Button>
          <Button variant="primary" size="sm" onClick={submit} disabled={create.isPending || !name}>
            {create.isPending ? 'Creating…' : 'Create account'}
          </Button>
        </>
      }
    >
      <form className="pf-modal-body" onSubmit={submit}>
        <div className="pf-formrow">
          <label htmlFor="b-name">Name</label>
          <Input id="b-name" value={name} onChange={(e) => setName(e.target.value)} required />
        </div>
        <div className="pf-formrow">
          <label htmlFor="b-inst">Institution</label>
          <Input id="b-inst" value={institution} onChange={(e) => setInstitution(e.target.value)} />
        </div>
        <div className="row gap12">
          <div className="pf-formrow grow">
            <label htmlFor="b-mask">Mask (last 4)</label>
            <Input
              id="b-mask"
              value={mask}
              maxLength={4}
              onChange={(e) => setMask(e.target.value)}
            />
          </div>
          <div className="pf-formrow grow">
            <label htmlFor="b-purpose">Purpose</label>
            <Select id="b-purpose" value={purpose} onChange={(e) => setPurpose(e.target.value)}>
              {BANK_PURPOSES.map((p) => (
                <option key={p} value={p}>
                  {p.charAt(0).toUpperCase() + p.slice(1)}
                </option>
              ))}
            </Select>
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
