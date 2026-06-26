import { useNavigate, useParams } from 'react-router-dom';
import { Card, CardHeader, Money, StatCard, Table, type TableColumn } from '@/design';
import { DetailPage } from '@/components/DetailPage';
import { num, useOwnerDetail, type OwnerDetail, type PropertyListRow } from '@/lib/directory';

const propertyColumns: TableColumn<PropertyListRow> = {
  key: 'address',
  header: 'Address',
  render: (r) => <span className="strong">{r.address}</span>,
};

export function OwnerDetailPage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  const query = useOwnerDetail(id);

  const columns: TableColumn<PropertyListRow>[] = [
    propertyColumns,
    { key: 'city', header: 'City', render: (r) => r.city ?? '—' },
    { key: 'units', header: 'Units', num: true, render: (r) => r.units },
    { key: 'occ', header: 'Occupied', num: true, render: (r) => `${r.occupied}/${r.units}` },
  ];

  return (
    <DetailPage<OwnerDetail>
      kind="owners"
      id={id}
      query={query}
      backTo="/owners"
      backLabel="Owners"
      toPath={(ownerId) => `/owners/${ownerId}`}
      title={(o) => o.name}
      sub={(o) =>
        o.defaultMgmtFeeBps != null
          ? `Default management fee · ${(num(o.defaultMgmtFeeBps) / 100).toFixed(2)}%`
          : 'No default management fee'
      }
    >
      {(o) => (
        <div className="col gap16">
          <div className="pf-statgrid">
            <StatCard
              label="Operating (distributable)"
              value={<Money value={num(o.operating)} big colorize />}
            />
            <StatCard label="Deposits held" value={<Money value={num(o.deposits)} big />} />
            <StatCard label="Total" value={<Money value={num(o.total)} big />} />
            <StatCard label="Reserve floor" value={<Money value={num(o.reserveAmount)} big />} />
          </div>

          <Card pad>
            <div className="pf-detail-grid">
              <div className="pf-kv">
                <span className="k">Email</span>
                <span className="v">{o.contact.email ?? '—'}</span>
              </div>
              <div className="pf-kv">
                <span className="k">Phone</span>
                <span className="v">{o.contact.phone ?? '—'}</span>
              </div>
            </div>
          </Card>

          <Card>
            <CardHeader
              title="Properties"
              sub={`${o.properties.length} on file. The owner statement arrives in M5.`}
            />
            <Table
              columns={columns}
              rows={o.properties}
              rowKey={(r) => r.id}
              onRowClick={(r) => navigate(`/properties/${r.id}`)}
            />
          </Card>
        </div>
      )}
    </DetailPage>
  );
}
