import { Badge, type BadgeTone, Button, Card, EmptyState, Money } from '@/design';
import { QueryErrorState } from '@/components/QueryErrorState';
import { num } from '@/lib/directory';
import { downloadReconciliationReport, useReconciliationHistory } from './banking';

const STATUS_TONE: Record<string, BadgeTone> = {
  in_progress: 'warn',
  finalized: 'pos',
  reopened: 'accent',
};

const STATUS_LABEL: Record<string, string> = {
  in_progress: 'In progress',
  finalized: 'Finalized',
  reopened: 'Reopened',
};

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** Past reconciliations for the account, newest first; finalized ones expose their stored report (P64). */
export function ReconciliationHistory({ bankAccountId }: { bankAccountId: string }) {
  const history = useReconciliationHistory(bankAccountId);

  return (
    <Card className="pf-ledger-card">
      <div className="pf-card-hd">
        <div>
          <h3>Reconciliation history</h3>
          {/*
            The body already separates pending, error, and empty; this count did not, so a failed
            or in-flight read reported a confirmed "0 reconciliations" above it. On an audit trail
            that is the difference between "nothing was finalized" and "we could not check".
          */}
          <div className="sub">
            {history.isSuccess
              ? `${history.data.length} reconciliations`
              : history.isError
                ? 'Count unavailable'
                : 'Counting…'}
          </div>
        </div>
      </div>

      {history.isPending ? (
        <div className="pf-pad col gap8">
          {[0, 1, 2].map((row) => (
            <div key={row} className="pf-skeleton" style={{ height: 20 }} />
          ))}
        </div>
      ) : history.isError ? (
        <div className="pf-pad">
          <QueryErrorState
            query={history}
            title="Couldn't load history"
            fallback="Failed to load the reconciliation history."
          />
        </div>
      ) : (history.data?.length ?? 0) === 0 ? (
        <div className="pf-pad">
          <EmptyState
            icon="doc"
            title="No reconciliations yet"
            description="Finalize a reconciliation and it appears here."
          />
        </div>
      ) : (
        <div className="pf-recon-hist">
          {history.data?.map((row) => (
            <div key={row.id} className="pf-recon-histrow">
              <div className="grow col gap2">
                <span className="fw6">
                  {MONTHS[num(row.month) - 1] ?? row.month} {row.year}
                </span>
                <span className="t3 fs12">
                  {row.finalizedAt ? `Finalized ${row.finalizedAt.slice(0, 10)}` : 'Not finalized'}
                </span>
              </div>
              <Money value={num(row.statementEndingBalance)} plain />
              <Badge tone={STATUS_TONE[row.status] ?? 'neutral'} soft dot>
                {STATUS_LABEL[row.status] ?? row.status}
              </Badge>
              {row.hasReport && (
                <Button
                  variant="ghost"
                  size="sm"
                  icon="download"
                  onClick={() => void downloadReconciliationReport(row.id)}
                >
                  Report
                </Button>
              )}
            </div>
          ))}
        </div>
      )}
    </Card>
  );
}
