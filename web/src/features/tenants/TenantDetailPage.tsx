import { useParams } from 'react-router-dom';
import { Card, CardHeader, Money } from '@/design';
import { DetailPage } from '@/lib/DetailPage';
import { LeaseStatusBadge, TenantStatusBadge } from '@/lib/StatusBadge';
import { num, useTenantDetail } from '@/lib/directory';

export function TenantDetailPage() {
  const { id = '' } = useParams();
  const query = useTenantDetail(id);

  return (
    <DetailPage
      kind="tenants"
      id={id}
      query={query}
      backTo="/tenants"
      backLabel="Tenants"
      toPath={(tenantId) => `/tenants/${tenantId}`}
      title={(t) => t.displayName}
      sub={(t) => <TenantStatusBadge status={t.status} />}
    >
      {(t) => (
        <div className="col gap16">
          <Card pad>
            <div className="pf-detail-grid">
              <div className="pf-kv"><span className="k">Unit</span><span className="v">{t.unitLabel ?? '—'}</span></div>
              <div className="pf-kv"><span className="k">Property</span><span className="v">{t.propertyAddress ?? '—'}</span></div>
              <div className="pf-kv"><span className="k">Owner</span><span className="v">{t.ownerName ?? '—'}</span></div>
              <div className="pf-kv"><span className="k">Email</span><span className="v">{t.contact.email ?? '—'}</span></div>
              <div className="pf-kv"><span className="k">Phone</span><span className="v">{t.contact.phone ?? '—'}</span></div>
            </div>
          </Card>

          <div className="pf-detail-grid">
            <Card pad>
              <p className="pf-section-title">Balance</p>
              <div style={{ fontSize: 26 }}><Money value={num(t.balance)} big colorize /></div>
              <p className="t3 fs13" style={{ marginTop: 6 }}>Receivable less any unapplied prepayment.</p>
            </Card>
            <Card pad>
              <p className="pf-section-title">Deposit held</p>
              <div style={{ fontSize: 26 }}><Money value={num(t.depositHeld)} big /></div>
              <p className="t3 fs13" style={{ marginTop: 6 }}>Liability · not income (recognized only on application).</p>
            </Card>
          </div>

          <Card>
            <CardHeader title="Lease" sub="Read-only in M2 — the inline ledger composer arrives in M3." />
            <div className="pf-pad">
              {t.lease ? (
                <div className="pf-detail-grid">
                  <div className="pf-kv"><span className="k">Term start</span><span className="v">{t.lease.startDate ?? '—'}</span></div>
                  <div className="pf-kv"><span className="k">Term end</span><span className="v">{t.lease.endDate ?? '—'}</span></div>
                  <div className="pf-kv"><span className="k">Rent</span><span className="v"><Money value={num(t.lease.rent)} /></span></div>
                  <div className="pf-kv"><span className="k">Deposit required</span><span className="v"><Money value={num(t.lease.depositRequired)} /></span></div>
                  <div className="pf-kv"><span className="k">Status</span><span className="v"><LeaseStatusBadge status={t.lease.status} /></span></div>
                </div>
              ) : (
                <p className="t3 fs13">No active lease on record.</p>
              )}
            </div>
          </Card>
        </div>
      )}
    </DetailPage>
  );
}
