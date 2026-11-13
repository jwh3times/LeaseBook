import { useQueryClient } from '@tanstack/react-query';
import { Fragment, useId, useState } from 'react';
import { Badge, type BadgeTone, Button, Card, EmptyState, Money } from '@/design';
import { QueryErrorState } from '@/components/QueryErrorState';
import { useSession } from '@/features/auth/useSession';
import { localDate } from '@/lib/dates';
import { num } from '@/lib/directory';
import {
  downloadReconciliationReport,
  reconciliationHistoryKey,
  type ReconciliationSummary,
  useReconciliationHistory,
} from './banking';
import { FinalizeAgainPanel } from './FinalizeAgainPanel';
import { UnlockReconciliationDialog } from './UnlockReconciliationDialog';

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

const periodLabel = (row: ReconciliationSummary) =>
  `${MONTHS[num(row.month) - 1] ?? row.month} ${row.year}`;

/**
 * Past reconciliations for the account, newest first; finalized ones expose their stored report (P64),
 * a PMAdmin can unlock one with a reason (#554), and staff finalize a reopened one again in place
 * (#656). The server enforces the role; hiding Unlock from
 * staff only keeps them from being offered what they cannot do.
 */
export function ReconciliationHistory({ bankAccountId }: { bankAccountId: string }) {
  const history = useReconciliationHistory(bankAccountId);
  const session = useSession();
  const queryClient = useQueryClient();
  const isAdmin = session.data?.role === 'PMAdmin';
  const [unlockTarget, setUnlockTarget] = useState<ReconciliationSummary | null>(null);
  // The reopened month being finalized again in place (#656); one at a time, since months finalize
  // in order.
  const [againId, setAgainId] = useState<string | null>(null);
  const baseId = useId();
  const againButtonId = (id: string) => `${baseId}-again-${id}`;
  const periodId = (id: string) => `${baseId}-period-${id}`;
  const closeAgain = (id: string) => {
    setAgainId(null);
    // The button stays on the row while its view is open, so the keyboard returns to where it left.
    document.getElementById(againButtonId(id))?.focus();
  };

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
            <Fragment key={row.id}>
              <div className="pf-recon-histrow">
                <div className="grow col gap2">
                  <span id={periodId(row.id)} tabIndex={-1} className="fw6">
                    {periodLabel(row)}
                  </span>
                  <span className="t3 fs12">
                    {row.finalizedAt ? `Finalized ${localDate(row.finalizedAt)}` : 'Not finalized'}
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
                {isAdmin && row.status === 'finalized' && (
                  <Button
                    variant="ghost"
                    size="sm"
                    icon="lock"
                    aria-label={`Unlock ${periodLabel(row)}`}
                    onClick={() => setUnlockTarget(row)}
                  >
                    Unlock
                  </Button>
                )}
                {row.status === 'reopened' && (
                  <Button
                    id={againButtonId(row.id)}
                    variant="ghost"
                    size="sm"
                    icon="check"
                    aria-label={`Finalize ${periodLabel(row)} again`}
                    aria-expanded={againId === row.id}
                    aria-controls={againId === row.id ? `${baseId}-panel-${row.id}` : undefined}
                    onClick={() => (againId === row.id ? closeAgain(row.id) : setAgainId(row.id))}
                  >
                    Finalize again
                  </Button>
                )}
              </div>
              {againId === row.id && (
                <FinalizeAgainPanel
                  id={`${baseId}-panel-${row.id}`}
                  reconciliation={row}
                  period={periodLabel(row)}
                  onClose={() => closeAgain(row.id)}
                  onFinalized={() => {
                    setAgainId(null);
                    // The view and its button leave together, so focus goes to the month it closed.
                    document.getElementById(periodId(row.id))?.focus();
                  }}
                />
              )}
            </Fragment>
          ))}
        </div>
      )}
      {unlockTarget && (
        <UnlockReconciliationDialog
          reconciliationId={unlockTarget.id}
          period={periodLabel(unlockTarget)}
          onClose={() => setUnlockTarget(null)}
          onUnlocked={() => {
            setUnlockTarget(null);
            void queryClient.invalidateQueries({
              queryKey: reconciliationHistoryKey(bankAccountId),
            });
          }}
        />
      )}
    </Card>
  );
}
