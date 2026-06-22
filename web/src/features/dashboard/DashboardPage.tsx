import { useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  Badge,
  Button,
  Card,
  CardHeader,
  EmptyState,
  Icon,
  Money,
  ProgressBar,
  StatCard,
} from '@/design';
import { num } from '@/lib/directory';
import { useDashboard, type DashboardResponse } from '@/lib/dashboard';
import { trackInteraction } from '@/lib/telemetry';

export function DashboardPage() {
  const navigate = useNavigate();
  const query = useDashboard();

  // Owner ending balances are visible with zero clicks (the PRD acceptance) — record the budget met.
  useEffect(() => {
    if (query.isSuccess) trackInteraction('owner-balances-visible', 0, true);
  }, [query.isSuccess]);

  if (query.isPending) return <DashboardSkeleton />;
  if (query.isError || !query.data) {
    return (
      <div className="pf-fade">
        <Card pad>
          <EmptyState
            icon="alert"
            title="Couldn’t load the dashboard"
            description="Try again in a moment."
          />
        </Card>
      </div>
    );
  }

  const d = query.data;
  const collectedPct =
    num(d.kpis.collectedTarget) > 0
      ? (num(d.kpis.collectedMtd) / num(d.kpis.collectedTarget)) * 100
      : 0;

  return (
    <div className="pf-fade">
      <div className="pf-pagehd">
        <div>
          <h2>Dashboard</h2>
        </div>
        <Button variant="primary" size="sm" icon="wallet" onClick={() => navigate('/operations')}>
          Run owner disbursements
        </Button>
      </div>

      <div className="pf-statgrid">
        <StatCard
          label="Trust total"
          value={<Money value={num(d.kpis.trustTotal)} big />}
          sub="Across all trust bank books"
        />
        <StatCard
          label="Owners payable"
          value={<Money value={num(d.kpis.ownersPayable)} big />}
          sub="Disbursable this cycle"
        />
        <StatCard
          label="Uncleared"
          value={<Money value={num(d.kpis.uncleared)} big />}
          sub={
            <Badge tone={num(d.kpis.unclearedCount) === 0 ? 'pos' : 'warn'} dot>
              {num(d.kpis.unclearedCount) === 0
                ? 'Reconciled'
                : `${num(d.kpis.unclearedCount)} items`}
            </Badge>
          }
        />
        <StatCard
          label="Collected this month"
          value={
            <div className="col gap8" style={{ width: '100%' }}>
              <span>
                <Money value={num(d.kpis.collectedMtd)} />{' '}
                <span className="t3 fs13">
                  of <Money value={num(d.kpis.collectedTarget)} />
                </span>
              </span>
              <ProgressBar pct={collectedPct} tone="pos" />
            </div>
          }
        />
      </div>

      <div className="pf-dash-grid">
        <OwnerBalancesHero data={d} onOpenOwner={(id) => navigate(`/owners/${id}`)} />
        <div className="col gap16">
          <BankSummary data={d} onReconcile={() => navigate('/banking')} />
          <ActionItems data={d} onGo={(route) => navigate(route)} />
        </div>
      </div>
    </div>
  );
}

function OwnerBalancesHero({
  data,
  onOpenOwner,
}: {
  data: DashboardResponse;
  onOpenOwner: (id: string) => void;
}) {
  return (
    <Card>
      <CardHeader title="Owner ending balances" sub="Server-computed · visible at a glance" />
      <table className="pf-table">
        <thead>
          <tr>
            <th>Owner</th>
            <th className="num">Operating</th>
            <th className="num">Deposits</th>
            <th className="num">Total</th>
          </tr>
        </thead>
        <tbody>
          {data.ownerBalances.rows.map((row) => (
            <tr
              key={row.ownerId}
              className={row.isRollup ? 'muted' : 't-row-click'}
              onClick={row.isRollup ? undefined : () => onOpenOwner(row.ownerId)}
            >
              <td className={row.isRollup ? undefined : 'strong'}>{row.name}</td>
              <td className="num">
                <Money value={num(row.operating)} colorize />
              </td>
              <td className="num">
                <Money value={num(row.deposits)} />
              </td>
              <td className="num">
                <Money value={num(row.total)} />
              </td>
            </tr>
          ))}
        </tbody>
        <tfoot>
          <tr className="strong">
            <td>Total</td>
            <td className="num">
              <Money value={num(data.ownerBalances.totals.operating)} />
            </td>
            <td className="num">
              <Money value={num(data.ownerBalances.totals.deposits)} />
            </td>
            <td className="num">
              <Money value={num(data.ownerBalances.totals.total)} />
            </td>
          </tr>
        </tfoot>
      </table>
    </Card>
  );
}

function BankSummary({ data, onReconcile }: { data: DashboardResponse; onReconcile: () => void }) {
  return (
    <Card pad>
      <div className="row" style={{ justifyContent: 'space-between', alignItems: 'baseline' }}>
        <p className="pf-section-title">Trust accounts</p>
        <Button variant="ghost" size="sm" icon="arrowUpRight" onClick={onReconcile}>
          Reconcile
        </Button>
      </div>
      {data.banks.rows.map((bank) => (
        <div
          key={bank.bankAccountId}
          className="pf-bankrow t-row-click"
          role="button"
          tabIndex={0}
          onClick={onReconcile}
          onKeyDown={(event) => {
            if (event.key === 'Enter') onReconcile();
          }}
        >
          <div className="col">
            <span className="fw6">{bank.name}</span>
            <Badge tone={num(bank.unclearedCount) === 0 ? 'pos' : 'warn'} dot>
              {num(bank.unclearedCount) === 0
                ? 'Reconciled'
                : `${num(bank.unclearedCount)} uncleared`}
            </Badge>
          </div>
          <Money value={num(bank.book)} />
        </div>
      ))}
    </Card>
  );
}

function ActionItems({ data, onGo }: { data: DashboardResponse; onGo: (route: string) => void }) {
  return (
    <Card pad>
      <p className="pf-section-title">Needs attention</p>
      {data.actionItems.map((item) => (
        <div
          key={item.id}
          className="pf-action"
          role="button"
          tabIndex={0}
          onClick={() => onGo(item.route)}
          onKeyDown={(event) => {
            if (event.key === 'Enter') onGo(item.route);
          }}
        >
          <span className={`dot ${item.kind}`} />
          <div className="grow">
            <div className="a-title">{item.title}</div>
            <div className="a-detail">{item.detail}</div>
          </div>
          <Icon name="chevronRight" size={16} />
        </div>
      ))}
    </Card>
  );
}

function DashboardSkeleton() {
  return (
    <div className="pf-fade">
      <div className="pf-pagehd">
        <div>
          <h2>Dashboard</h2>
        </div>
      </div>
      <div className="pf-statgrid">
        {Array.from({ length: 4 }).map((_, index) => (
          <Card key={index} pad>
            <div className="pf-skeleton" style={{ height: 40 }} />
          </Card>
        ))}
      </div>
      <Card pad>
        <div className="col gap12">
          {Array.from({ length: 6 }).map((_, i) => (
            <div key={i} className="pf-skeleton" />
          ))}
        </div>
      </Card>
    </div>
  );
}
