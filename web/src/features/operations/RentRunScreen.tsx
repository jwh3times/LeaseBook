/**
 * Rent charge run screen (M6 WP-5).
 * All eligible leases are selected — this run is all-or-nothing for the period.
 * Preview → confirm in 2 clicks (period + Confirm): within the budgeted click depth.
 */
import { useState } from 'react';
import { Button, Card, CardHeader, EmptyState } from '@/design';
import { trackInteraction } from '@/lib/telemetry';
import { PeriodPicker } from './PeriodPicker';
import { currentPeriod } from './periodUtils';
import { RunPreviewGrid, RunResultPanel } from './RunPreviewGrid';
import { useConfirmRun, useRunPreview } from './useRuns';
import type { RunResultSpaResponse } from './useRuns';

export function RentRunScreen() {
  const [period, setPeriod] = useState(currentPeriod);
  const [result, setResult] = useState<RunResultSpaResponse | null>(null);
  const [confirmError, setConfirmError] = useState<string | null>(null);

  const preview = useRunPreview('rent', period.year, period.month);
  const confirm = useConfirmRun('rent');

  const handleConfirm = () => {
    if (!preview.data) return;
    const eligible = preview.data.rows
      .filter((r) => !r.excludedReason && !r.alreadyDone)
      .map((r) => r.targetId);

    // Confirming rent = 2 clicks: change period (optional) + Confirm button.
    trackInteraction('rent-run-confirm', 2, true);

    confirm.mutate(
      { year: period.year, month: period.month, selectedTargetIds: eligible },
      {
        onSuccess: (data) => {
          setResult(data);
          setConfirmError(null);
        },
        onError: (err) => setConfirmError(err.message),
      },
    );
  };

  if (result) {
    return (
      <RunResultPanel
        posted={Number(result.posted)}
        skipped={Number(result.skipped)}
        excluded={Number(result.excluded)}
        total={Number(result.total)}
        onDone={() => setResult(null)}
      />
    );
  }

  const eligibleCount =
    preview.data?.rows.filter((r) => !r.excludedReason && !r.alreadyDone).length ?? 0;
  const allSelected = new Set(
    preview.data?.rows.filter((r) => !r.excludedReason && !r.alreadyDone).map((r) => r.targetId) ??
      [],
  );

  return (
    <div className="col gap16">
      <Card pad>
        <div className="row" style={{ justifyContent: 'space-between', alignItems: 'center' }}>
          <CardHeader
            title="Rent charge run"
            sub="Post rent charges for all active leases in the period."
          />
          <PeriodPicker
            year={period.year}
            month={period.month}
            onChange={(y, m) => setPeriod({ year: y, month: m })}
          />
        </div>
      </Card>

      <Card pad>
        {preview.isPending ? (
          <div className="col gap8">
            {[0, 1, 2, 3].map((i) => (
              <div key={i} className="pf-skeleton" style={{ height: 20 }} />
            ))}
          </div>
        ) : preview.isError ? (
          <EmptyState
            icon="alert"
            title="Couldn't load preview"
            description="Please retry in a moment."
          />
        ) : (
          <>
            <RunPreviewGrid
              rows={preview.data?.rows ?? []}
              exceptions={preview.data?.exceptions ?? []}
              selected={allSelected}
              selectable={false}
            />
            {confirmError && (
              <p className="pf-composer-error" role="alert" style={{ marginTop: 8 }}>
                {confirmError}
              </p>
            )}
            <div className="row gap10" style={{ marginTop: 16 }}>
              <Button
                variant="primary"
                disabled={eligibleCount === 0 || confirm.isPending}
                onClick={handleConfirm}
              >
                {confirm.isPending
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
