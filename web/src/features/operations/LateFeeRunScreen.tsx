/**
 * Late-fee run screen (M6 WP-5).
 * Selective — the operator ticks which delinquent leases to charge. The flow itself (preview,
 * selection, confirm, conflict, outcome, interaction count) is `useRunFlow`'s.
 */
import { Button, Card, CardHeader } from '@/design';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { QueryErrorState } from '@/components/QueryErrorState';
import { PeriodPicker } from './PeriodPicker';
import { RunConflictNotice } from './RunConflictNotice';
import { RunPreviewGrid, RunResultPanel } from './RunPreviewGrid';
import { useRunFlow } from './useRunFlow';

export function LateFeeRunScreen() {
  const flow = useRunFlow('latefee', 'selective');
  const { period, preview, result, selected } = flow;

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

  return (
    <div className="col gap16">
      <Card pad>
        <div className="row" style={{ justifyContent: 'space-between', alignItems: 'center' }}>
          <CardHeader
            title="Late fee run"
            sub="Selectively charge late fees on delinquent leases."
          />
          <PeriodPicker year={period.year} month={period.month} onChange={flow.setPeriod} />
        </div>
      </Card>

      <Card pad>
        <RunConflictNotice show={flow.conflicted} mode="selective" />
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
              selected={selected}
              selectable
              onToggle={flow.toggle}
              onToggleAll={flow.toggleAll}
              issuedCoverage={{ type: 'latefee', year: period.year, month: period.month }}
            />
            <ApiErrorNotice error={flow.error} style={{ marginTop: 8 }} />
            <div className="row gap10" style={{ marginTop: 16 }}>
              <Button
                variant="primary"
                disabled={selected.size === 0 || flow.isConfirming || flow.isRefreshing}
                onClick={flow.confirm}
              >
                {flow.isConfirming
                  ? 'Posting…'
                  : selected.size === 0
                    ? 'Select leases to charge'
                    : `Confirm — charge ${selected.size} lease${selected.size === 1 ? '' : 's'}`}
              </Button>
            </div>
          </>
        )}
      </Card>
    </div>
  );
}
