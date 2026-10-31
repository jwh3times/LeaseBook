import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
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
 * and a PMAdmin can unlock one with a reason (#554). The server enforces the role; hiding Unlock from
 * staff only keeps them from being offered what they cannot do.
 */
export function ReconciliationHistory({ bankAccountId }: { bankAccountId: string }) {
  const history = useReconciliationHistory(bankAccountId);
  const session = useSession();
  const queryClient = useQueryClient();
  const isAdmin = session.data?.role === 'PMAdmin';
  const [unlockTarget, setUnlockTarget] = useState<ReconciliationSummary | null>(null);

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
                <span className="fw6">{periodLabel(row)}</span>
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
            </div>
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
