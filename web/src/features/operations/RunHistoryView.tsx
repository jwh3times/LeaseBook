/**
 * Run history view — lists past bulk runs (all types) for this org, most recent first.
 * Each run shows: type badge, period, summary counts, and total posted.
 */
import { Badge, Card, CardHeader, EmptyState, Money } from '@/design';
import { useRunHistory } from './useRuns';
import type { BadgeTone } from '@/design';

const RUN_TYPE_META: Record<string, { label: string; tone: BadgeTone }> = {
  Rent: { label: 'Rent', tone: 'accent' },
  LateFee: { label: 'Late fee', tone: 'warn' },
  Disbursement: { label: 'Disbursement', tone: 'pos' },
};

function runTypeMeta(runType: string): { label: string; tone: BadgeTone } {
  return RUN_TYPE_META[runType] ?? { label: runType, tone: 'neutral' };
}

function parseSummary(json: string | null | undefined): {
  posted?: number;
  skipped?: number;
  excluded?: number;
  total?: number;
} {
  try {
    return json
      ? (JSON.parse(json) as {
          posted?: number;
          skipped?: number;
          excluded?: number;
          total?: number;
        })
      : {};
  } catch {
    return {};
  }
}

function formatPeriod(year: number | string, month: number | string): string {
  const y = Number(year);
  const m = Number(month);
  const date = new Date(y, m - 1, 1);
  return date.toLocaleString('default', { month: 'long', year: 'numeric' });
}

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
        <EmptyState
          icon="alert"
          title="Couldn't load run history"
          description="Please retry in a moment."
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
                  <td>{formatPeriod(run.periodYear, run.periodMonth)}</td>
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
