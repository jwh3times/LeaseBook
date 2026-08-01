import { useEffect, useState } from 'react';
import { Badge, Button, Card, CardHeader, Input, Select, Table, type TableColumn } from '@/design';
import { Modal } from '@/components/Modal';
import {
  useBankAccounts,
  useCreateBankAccount,
  useOrgSettings,
  useSetBankAccountActive,
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
          <LateFeeForm initial={settings.data} />
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

/**
 * Org-default late-fee policy (WP-6). These five fields drive the late-fee run for every lease that
 * has not overridden them; a lease may override any field individually, edited from that tenant's
 * ledger page (Tenants → tenant → Late fees).
 *
 * Rate is entered as a percentage but stored in basis points, matching the M2 fee-config convention.
 * The NC §42-46 statutory cap is shown as read-only context, deliberately: it is computed per lease
 * in `LateFeeCalculator` from that lease's rent, so it is neither a stored field nor settable here.
 * Surfacing it stops the configured fee from reading as the amount that will actually be charged.
 */
function LateFeeForm({ initial }: { initial: OrgSettings }) {
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
      // The profile fields are replaced unconditionally by the handler (the late-fee fields are
      // patch-style), so they must be carried through or saving a late fee would blank the org's
      // legal name, address and phone.
      legalName: initial.legalName ?? null,
      address: initial.address ?? null,
      city: initial.city ?? null,
      state: initial.state ?? null,
      zip: initial.zip ?? null,
      phone: initial.phone ?? null,
      logoBlobRef: initial.logoBlobRef ?? null,
      accountingBasis: initial.accountingBasis,
      moneyNegativeDisplay: initial.moneyNegativeDisplay,

      rentDueDay: Number(form.rentDueDay),
      lateFeeGraceDays: Number(form.lateFeeGraceDays),
      lateFeeKind: form.lateFeeKind,
      lateFeeAmount: Number(form.lateFeeAmount),
      lateFeeRateBps: Number(form.lateFeeRateBps),
    });
    setSaved(true);
  }

  const isPercent = form.lateFeeKind === 'percent';

  return (
    <Card>
      <CardHeader
        title="Late fees"
        sub="Org defaults for the late-fee run. Individual leases can override any field."
      />
      <form className="pf-pad col gap14" onSubmit={save}>
        <div className="row gap12 wrap">
          <div className="pf-formrow" style={{ width: 150 }}>
            <label htmlFor="lf-due">Rent due day</label>
            <Input
              id="lf-due"
              type="number"
              min={1}
              max={28}
              className="pf-num"
              value={form.rentDueDay ?? ''}
              onChange={(e) => set('rentDueDay', Number(e.target.value))}
            />
            <span className="t3 fs12">Day of month, 1–28.</span>
          </div>
          <div className="pf-formrow" style={{ width: 150 }}>
            <label htmlFor="lf-grace">Grace days</label>
            <Input
              id="lf-grace"
              type="number"
              min={0}
              className="pf-num"
              value={form.lateFeeGraceDays ?? ''}
              onChange={(e) => set('lateFeeGraceDays', Number(e.target.value))}
            />
            <span className="t3 fs12">Days after the due date before a fee applies.</span>
          </div>
        </div>

        <div className="row gap12 wrap">
          <div className="pf-formrow grow">
            <label htmlFor="lf-kind">Fee type</label>
            <Select
              id="lf-kind"
              value={form.lateFeeKind}
              onChange={(e) => set('lateFeeKind', e.target.value)}
            >
              <option value="flat">Flat amount</option>
              <option value="percent">Percent of rent</option>
            </Select>
          </div>
          {isPercent ? (
            <div className="pf-formrow" style={{ width: 150 }}>
              <label htmlFor="lf-rate">Rate</label>
              <Input
                id="lf-rate"
                type="number"
                min={0}
                max={100}
                step={0.01}
                className="pf-num"
                // Stored in basis points; shown as a percentage.
                value={(Number(form.lateFeeRateBps ?? 0) / 100).toString()}
                onChange={(e) => set('lateFeeRateBps', Math.round(Number(e.target.value) * 100))}
              />
              <span className="t3 fs12">Percent of monthly rent.</span>
            </div>
          ) : (
            <div className="pf-formrow" style={{ width: 150 }}>
              <label htmlFor="lf-amount">Flat fee</label>
              <Input
                id="lf-amount"
                type="number"
                min={0}
                step={0.01}
                className="pf-num"
                value={form.lateFeeAmount ?? ''}
                onChange={(e) => set('lateFeeAmount', Number(e.target.value))}
              />
              <span className="t3 fs12">Charged once per late period.</span>
            </div>
          )}
        </div>

        <p className="t3 fs13">
          North Carolina caps a residential late fee at the greater of $15.00 or 5% of the monthly
          rent (NC §42-46). The cap is applied automatically per lease when the run computes a fee,
          so a configured fee above it is charged at the cap.
        </p>

        <div className="row gap12">
          <Button variant="primary" size="sm" onClick={save} disabled={update.isPending}>
            {update.isPending ? 'Saving…' : 'Save late-fee policy'}
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

function BankAccountsSection() {
  const banks = useBankAccounts();
  const setActive = useSetBankAccountActive();
  const [showNew, setShowNew] = useState(false);
  const [rowError, setRowError] = useState<string | null>(null);

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
    {
      key: 'status',
      header: 'Status',
      render: (b) => (
        <Badge tone={b.isActive ? 'pos' : 'neutral'} dot>
          {b.isActive ? 'Active' : 'Inactive'}
        </Badge>
      ),
    },
    {
      key: 'actions',
      header: '',
      render: (b) => (
        <Button
          variant="ghost"
          size="sm"
          onClick={async () => {
            setRowError(null);
            try {
              await setActive.mutateAsync({ id: b.id, isActive: !b.isActive });
            } catch {
              setRowError('Clear or reconcile outstanding items before deactivating this account.');
            }
          }}
        >
          {b.isActive ? 'Deactivate' : 'Reactivate'}
        </Button>
      ),
    },
  ];

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
      {rowError && (
        <div className="pf-pad err" role="alert">
          {rowError}
        </div>
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
