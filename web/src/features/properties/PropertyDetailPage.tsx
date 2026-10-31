import { useState } from 'react';
import { useNavigate, useParams } from 'react-router';
import { Button, Card, CardHeader, Money, Table, type TableColumn } from '@/design';
import { DetailPage } from '@/components/DetailPage';
import { InfoNotice } from '@/components/InfoNotice';
import {
  TenantFinancialStandingBadges,
  TenantLifecycleBadge,
  UnitAvailabilityBadge,
  UnitOccupancyBadge,
} from '@/components/StatusBadge';
import {
  num,
  usePropertyDetail,
  type PropertyDetail,
  type TenantListRow,
  type UnitRow,
} from '@/lib/directory';
import { TransferOwnershipDialog, type TransferredOwnership } from './TransferOwnershipDialog';

const unitColumns: TableColumn<UnitRow>[] = [
  { key: 'label', header: 'Unit', render: (r) => <span className="strong">{r.label}</span> },
  { key: 'rent', header: 'Rent', num: true, render: (r) => <Money value={num(r.rent)} /> },
  {
    key: 'occupancy',
    header: 'Occupancy',
    render: (r) => <UnitOccupancyBadge occupancy={r.occupancy} />,
  },
  {
    key: 'availability',
    header: 'Availability',
    render: (r) => <UnitAvailabilityBadge availability={r.availability} />,
  },
];

export function PropertyDetailPage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  const query = usePropertyDetail(id);
  const [transferring, setTransferring] = useState(false);
  const [transferred, setTransferred] = useState<TransferredOwnership | null>(null);

  const tenantColumns: TableColumn<TenantListRow>[] = [
    {
      key: 'name',
      header: 'Tenant',
      render: (r) => <span className="strong">{r.displayName}</span>,
    },
    { key: 'unit', header: 'Unit', render: (r) => r.unitLabel ?? '—' },
    {
      key: 'balance',
      header: 'Balance',
      num: true,
      render: (r) => <Money value={num(r.balance)} colorize />,
    },
    {
      key: 'lifecycle',
      header: 'Lifecycle',
      render: (r) => <TenantLifecycleBadge status={r.lifecycleStatus} />,
    },
    {
      key: 'standing',
      header: 'Financial standing',
      render: (r) => (
        <TenantFinancialStandingBadges
          delinquentBalance={num(r.financialStanding.delinquentBalance)}
          unappliedCredit={num(r.financialStanding.unappliedCredit)}
        />
      ),
    },
  ];

  return (
    <DetailPage<PropertyDetail>
      kind="properties"
      id={id}
      query={query}
      backTo="/properties"
      backLabel="Properties"
      toPath={(propertyId) => `/properties/${propertyId}`}
      title={(p) => p.address}
      sub={(p) => `${[p.city, p.state, p.zip].filter(Boolean).join(', ')} · Owner: ${p.ownerName}`}
      actions={() => (
        <Button variant="ghost" size="sm" icon="owners" onClick={() => setTransferring(true)}>
          Transfer ownership
        </Button>
      )}
    >
      {(p) => (
        <div className="col gap16">
          {transferring && (
            <TransferOwnershipDialog
              property={p}
              onClose={() => setTransferring(false)}
              onTransferred={(transfer) => {
                setTransferring(false);
                setTransferred(transfer);
              }}
            />
          )}
          {/* The route element is reused across properties, so the notice names the one it was for. */}
          {transferred?.result.propertyId === p.id && (
            <InfoNotice onDismiss={() => setTransferred(null)}>
              Transferred to {transferred.ownerName}, effective {transferred.result.effectiveDate}.{' '}
              {transferred.result.depositEntryId
                ? 'The held security deposits moved with it.'
                : 'No security deposits were held for this property, so none moved.'}
            </InfoNotice>
          )}
          <Card>
            <CardHeader title="Units" sub={`${p.units.length} unit(s)`} />
            {p.units.length > 0 ? (
              <Table columns={unitColumns} rows={p.units} rowKey={(r) => r.id} />
            ) : (
              <div className="pf-pad t3 fs13">No units on this property yet.</div>
            )}
          </Card>

          <Card>
            <CardHeader title="Tenants" sub={`${p.tenants.length} current tenant(s)`} />
            {p.tenants.length > 0 ? (
              <Table
                columns={tenantColumns}
                rows={p.tenants}
                rowKey={(r) => r.id}
                onRowClick={(r) => navigate(`/tenants/${r.id}`)}
              />
            ) : (
              <div className="pf-pad t3 fs13">No current tenants.</div>
            )}
          </Card>
        </div>
      )}
    </DetailPage>
  );
}
