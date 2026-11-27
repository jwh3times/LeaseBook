/**
 * Run history view — lists past bulk runs (all types) for this org, most recent first.
 * Each run shows: type badge, period, summary counts, and total posted. The period opens the run
 * (#573), and as a link it is reached and followed from the keyboard.
 */
import { Link } from 'react-router';
import { Badge, Card, CardHeader, EmptyState, Money } from '@/design';
import { QueryErrorState } from '@/components/QueryErrorState';
import { formatPeriod, parseSummary, runTypeMeta } from './runFormat';
import { useRunHistory } from './useRuns';

export function RunHistoryView() {
  const history = useRunHistory();

  return (
    <Card pad>
      <CardHeader title="Run history" sub="All completed bulk runs for this organisation" />

      {history.isPending ? (
        <div className="col gap8">
          {[0, 1, 2].map((i) => (
            <div key={i} className="pf-skeleton" style={{ height: 20 }} />
          ))}
        </div>
      ) : history.isError ? (
        <QueryErrorState
          query={history}
          title="Couldn't load run history"
          fallback="Failed to load the run history."
        />
      ) : (history.data?.runs ?? []).length === 0 ? (
        <EmptyState
          icon="doc"
          title="No runs yet"
          description="Completed rent, late-fee, and disbursement runs will appear here."
        />
      ) : (
        <table className="pf-table">
          <thead>
            <tr>
              <th>Type</th>
              <th>Period</th>
              <th className="num">Posted</th>
              <th className="num">Skipped</th>
              <th className="num">Excluded</th>
              <th className="num">Total</th>
              <th>Run at</th>
            </tr>
          </thead>
          <tbody>
            {(history.data?.runs ?? []).map((run) => {
              const meta = runTypeMeta(run.runType);
              const summary = parseSummary(run.summaryJson);
              const createdAt = new Date(run.createdAt).toLocaleString();
              return (
                <tr key={run.id}>
                  <td>
                    <Badge tone={meta.tone}>{meta.label}</Badge>
                  </td>
                  <td>
                    <Link
                      to={`/operations/runs/${run.id}`}
                      aria-label={`${meta.label} run, ${formatPeriod(run.periodYear, run.periodMonth)}`}
                    >
                      {formatPeriod(run.periodYear, run.periodMonth)}
                    </Link>
                  </td>
                  <td className="num">{summary.posted ?? 0}</td>
                  <td className="num">{summary.skipped ?? 0}</td>
                  <td className="num">{summary.excluded ?? 0}</td>
                  <td className="num">
                    <Money value={Number(summary.total ?? 0)} />
                  </td>
                  <td className="t3 fs12">{createdAt}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </Card>
  );
}
