/**
 * What one confirmed bulk run did to each target it touched (#573): its run outcome (CONTEXT.md),
 * the reason for every target that did not post, and who the target is today. Opened from a row in
 * run history and from the panel shown after a run is confirmed.
 *
 * The server orders the items (excluded, then skipped, then posted; by name within each; targets it
 * cannot find last), so this page keeps that order and only narrows it by outcome. The counts are
 * taken from those same items, so the header and the filter can never disagree; the total is the
 * server's, since it is money and is summed exactly there.
 */
import { useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { isNotFound, type BulkRunDetailResponse, type BulkRunItemSpa } from '@/api';
import { IssuedStatementNotice } from '@/components/IssuedStatementNotice';
import { QueryErrorState } from '@/components/QueryErrorState';
import { RunOutcomeBadge } from '@/components/StatusBadge';
import { Button, Card, CardHeader, EmptyState, FilterChip, Money } from '@/design';
import {
  RUN_OUTCOMES,
  formatPeriod,
  parseSummary,
  runTypeMeta,
  type RunOutcome,
} from './runFormat';
import { runReasonLabel } from './runReasons';
import { useRunDetail } from './useRuns';

type Filter = 'All' | RunOutcome;

const FILTERS: readonly Filter[] = ['All', ...RUN_OUTCOMES];

const matches = (filter: Filter) => (item: BulkRunItemSpa) =>
  filter === 'All' || item.outcome === filter;

export function RunDetailPage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  const detail = useRunDetail(id);

  return (
    <div className="pf-fade">
      <div className="row gap8 mb16">
        <Button
          variant="ghost"
          size="sm"
          icon="chevronLeft"
          onClick={() => navigate('/operations?tab=history')}
        >
          Run history
        </Button>
      </div>

      {detail.isError && isNotFound(detail.error) ? (
        <Card pad>
          <EmptyState
            icon="alert"
            title="Run not found"
            description="The link may be wrong. Open the run from run history instead."
          />
        </Card>
      ) : detail.isError ? (
        <Card pad>
          <QueryErrorState
            query={detail}
            title="Couldn’t load this run"
            fallback="Failed to load the run."
          />
        </Card>
      ) : detail.isPending ? (
        <Card pad>
          <div className="col gap8" aria-busy="true" aria-label="Loading the run">
            <div className="pf-skeleton" style={{ maxWidth: 280, height: 26 }} />
            {[0, 1, 2].map((i) => (
              <div key={i} className="pf-skeleton" style={{ height: 20 }} />
            ))}
          </div>
        </Card>
      ) : (
        <RunDetail detail={detail.data} />
      )}
    </div>
  );
}

function RunDetail({ detail }: { detail: BulkRunDetailResponse }) {
  const { run, items } = detail;
  const [filter, setFilter] = useState<Filter>('All');
  const [noticeDismissed, setNoticeDismissed] = useState(false);
  const count = (f: Filter) => items.filter(matches(f)).length;
  const shown = items.filter(matches(filter));
  const total = parseSummary(run.summaryJson).total;

  return (
    <div className="col gap16">
      <div className="pf-detailhd">
        <div>
          <h2>
            {runTypeMeta(run.runType).label} run — {formatPeriod(run.periodYear, run.periodMonth)}
          </h2>
          <p className="sub">Run at {new Date(run.createdAt).toLocaleString()}</p>
        </div>
      </div>

      <Card pad>
        <div className="col gap8">
          <div className="row gap16" role="group" aria-label="Run summary">
            <span>
              <span className="fw6">{count('Posted')}</span> posted
            </span>
            <span>
              <span className="fw6">{count('Skipped')}</span> skipped
            </span>
            <span>
              <span className="fw6">{count('Excluded')}</span> excluded
            </span>
            <span>{total === undefined ? '—' : <Money value={Number(total)} />} total</span>
          </div>
          {/* As after confirming (#377): a run that posted may have landed in a month whose owner
              statement was already issued. A run that posted nothing cannot have. */}
          {count('Posted') > 0 && !noticeDismissed && (
            <IssuedStatementNotice
              target={{ runId: run.id }}
              onDismiss={() => setNoticeDismissed(true)}
            />
          )}
        </div>
      </Card>

      <Card pad>
        <CardHeader
          title="Targets"
          sub="What this run did to each tenant or owner it was run for"
        />
        {items.length === 0 ? (
          <EmptyState
            icon="doc"
            title="This run recorded no targets"
            description="Nothing was selected when it was confirmed."
          />
        ) : (
          <div className="col gap12">
            <div className="row gap8" role="group" aria-label="Show targets by outcome">
              {FILTERS.map((f) => (
                <FilterChip key={f} active={filter === f} onClick={() => setFilter(f)}>
                  {f} ({count(f)})
                </FilterChip>
              ))}
            </div>
            {shown.length === 0 ? (
              <EmptyState icon="doc" title={`No ${filter.toLowerCase()} targets in this run`} />
            ) : (
              <table className="pf-table">
                <thead>
                  <tr>
                    <th>Target</th>
                    <th>Outcome</th>
                    <th className="num">Amount</th>
                    <th>Reason</th>
                  </tr>
                </thead>
                <tbody>
                  {shown.map((item) => (
                    <tr key={item.id}>
                      <td>
                        <Target item={item} />
                      </td>
                      <td>
                        <RunOutcomeBadge outcome={item.outcome} />
                      </td>
                      <td className="num">
                        <Money value={Number(item.amount)} />
                      </td>
                      <td>{item.reason ? runReasonLabel(item.reason) : null}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        )}
      </Card>
    </div>
  );
}

/** Where a found target links: a lease to its tenant's ledger, an owner to the owner's page. */
function targetPath(item: BulkRunItemSpa): string | null {
  if (item.targetName === null) {
    return null;
  }
  if (item.targetKind === 'Owner') {
    return `/owners/${item.targetId}`;
  }
  return item.tenantId === null ? null : `/tenants/${item.tenantId}`;
}

/** Who the target is today, linked to its ledger or page; said plainly when it cannot be found. */
function Target({ item }: { item: BulkRunItemSpa }) {
  if (item.targetName === null) {
    return (
      <span className="t3">
        {item.targetKind === 'Lease' ? 'Lease not found' : 'Owner not found'}
      </span>
    );
  }

  const to = targetPath(item);
  return (
    <span>
      {to === null ? (
        <span className="strong">{item.targetName}</span>
      ) : (
        <Link to={to} className="strong">
          {item.targetName}
        </Link>
      )}
      {item.unitLabel ? <span className="t3"> · {item.unitLabel}</span> : null}
    </span>
  );
}
