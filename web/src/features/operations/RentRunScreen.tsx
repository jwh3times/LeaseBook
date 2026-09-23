/**
 * Rent charge run screen (M6 WP-5).
 * All eligible leases are posted — this run is all-or-nothing for the period. The flow itself
 * (preview, confirm, conflict, outcome, interaction count) is `useRunFlow`'s.
 */
import { Button, Card, CardHeader } from '@/design';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { QueryErrorState } from '@/components/QueryErrorState';
import { PeriodPicker } from './PeriodPicker';
import { RunConflictNotice } from './RunConflictNotice';
import { RunPreviewGrid, RunResultPanel } from './RunPreviewGrid';
import { useRunFlow } from './useRunFlow';

export function RentRunScreen() {
  const flow = useRunFlow('rent', 'all-eligible');
  const { period, preview, result } = flow;

  if (result) {
    return (
      <RunResultPanel
        runId={result.runId}
        posted={Number(result.posted)}
        skipped={Number(result.skipped)}
        excluded={Number(result.excluded)}
        total={Number(result.total)}
        onDone={flow.done}
      />
    );
  }

  const eligibleCount = flow.eligibleIds.length;

  return (
    <div className="col gap16">
      <Card pad>
        <div className="row" style={{ justifyContent: 'space-between', alignItems: 'center' }}>
          <CardHeader
            title="Rent charge run"
            sub="Post rent charges for all active leases in the period."
          />
          <PeriodPicker year={period.year} month={period.month} onChange={flow.setPeriod} />
        </div>
      </Card>

      <Card pad>
        <RunConflictNotice show={flow.conflicted} mode="all-eligible" />
        {preview.isPending ? (
          <div className="col gap8">
            {[0, 1, 2, 3].map((i) => (
              <div key={i} className="pf-skeleton" style={{ height: 20 }} />
            ))}
          </div>
        ) : preview.isError ? (
          <QueryErrorState
            query={preview}
            title="Couldn't load preview"
            fallback="Failed to load the run preview."
            onRetry={flow.retry}
          />
        ) : (
          <>
            <RunPreviewGrid
              rows={preview.data?.rows ?? []}
              exceptions={preview.data?.exceptions ?? []}
              selected={flow.selected}
              selectable={false}
              issuedCoverage={{ type: 'rent', year: period.year, month: period.month }}
            />
            <ApiErrorNotice error={flow.error} style={{ marginTop: 8 }} />
            <div className="row gap10" style={{ marginTop: 16 }}>
              <Button
                variant="primary"
                disabled={eligibleCount === 0 || flow.isConfirming || flow.isRefreshing}
                onClick={flow.confirm}
              >
                {flow.isConfirming
                  ? 'Posting…'
                  : eligibleCount === 0
                    ? 'Nothing to post'
                    : `Confirm — post ${eligibleCount} charges`}
              </Button>
            </div>
          </>
        )}
      </Card>
    </div>
  );
}
