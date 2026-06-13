import { useNavigate, useParams } from 'react-router-dom';
import { Card, CardHeader, Money, Table, type TableColumn } from '@/design';
import { DetailPage } from '@/lib/DetailPage';
import { TenantStatusBadge, UnitStatusBadge } from '@/lib/StatusBadge';
import { num, usePropertyDetail, type PropertyDetail, type TenantListRow, type UnitRow } from '@/lib/directory';

const unitColumns: TableColumn<UnitRow>[] = [
  { key: 'label', header: 'Unit', render: (r) => <span className="strong">{r.label}</span> },
  { key: 'rent', header: 'Rent', num: true, render: (r) => <Money value={num(r.rent)} /> },
  { key: 'status', header: 'Status', render: (r) => <UnitStatusBadge status={r.status} /> },
];

export function PropertyDetailPage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  const query = usePropertyDetail(id);

  const tenantColumns: TableColumn<TenantListRow>[] = [
    { key: 'name', header: 'Tenant', render: (r) => <span className="strong">{r.displayName}</span> },
    { key: 'unit', header: 'Unit', render: (r) => r.unitLabel ?? '—' },
    { key: 'balance', header: 'Balance', num: true, render: (r) => <Money value={num(r.balance)} colorize /> },
    { key: 'status', header: 'Status', render: (r) => <TenantStatusBadge status={r.status} /> },
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
    >
      {(p) => (
        <div className="col gap16">
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
              <Table columns={tenantColumns} rows={p.tenants} rowKey={(r) => r.id} onRowClick={(r) => navigate(`/tenants/${r.id}`)} />
            ) : (
              <div className="pf-pad t3 fs13">No current tenants.</div>
            )}
          </Card>
        </div>
      )}
    </DetailPage>
  );
}
