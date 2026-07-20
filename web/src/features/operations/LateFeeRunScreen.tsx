/**
 * Late-fee run screen (M6 WP-5).
 * Selective — operator picks which delinquent leases to charge. Checkboxes per row.
 * Preview → check boxes → confirm: within the budgeted click depth.
 */
import { useState } from 'react';
import { Button, Card, CardHeader, EmptyState } from '@/design';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { trackInteraction } from '@/lib/telemetry';
import { PeriodPicker } from './PeriodPicker';
import { currentPeriod } from './periodUtils';
import { RunPreviewGrid, RunResultPanel } from './RunPreviewGrid';
import { useConfirmRun, useRunPreview } from './useRuns';
import type { RunError, RunResultSpaResponse } from './useRuns';

export function LateFeeRunScreen() {
  const [period, setPeriod] = useState(currentPeriod);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [result, setResult] = useState<RunResultSpaResponse | null>(null);
  const [confirmError, setConfirmError] = useState<RunError | null>(null);

  const preview = useRunPreview('latefee', period.year, period.month);
  const confirm = useConfirmRun('latefee');

  // When period changes, reset selection.
  const handlePeriodChange = (y: number, m: number) => {
    setPeriod({ year: y, month: m });
    setSelected(new Set());
  };

  const eligibleIds =
    preview.data?.rows.filter((r) => !r.excludedReason && !r.alreadyDone).map((r) => r.targetId) ??
    [];

  const handleToggle = (id: string) => {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const handleToggleAll = () => {
    const allSelected = eligibleIds.length > 0 && eligibleIds.every((id) => selected.has(id));
    if (allSelected) {
      setSelected(new Set());
    } else {
      setSelected(new Set(eligibleIds));
    }
  };

  const handleConfirm = () => {
    trackInteraction('latefee-run-confirm', 2, true);
    confirm.mutate(
      { year: period.year, month: period.month, selectedTargetIds: Array.from(selected) },
      {
        onSuccess: (data) => {
          setResult(data);
          setConfirmError(null);
        },
        onError: (err) => setConfirmError(err),
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
        onDone={() => {
          setResult(null);
          setSelected(new Set());
        }}
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
          <PeriodPicker year={period.year} month={period.month} onChange={handlePeriodChange} />
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
              selected={selected}
              selectable
              onToggle={handleToggle}
              onToggleAll={handleToggleAll}
            />
            <ApiErrorNotice error={confirmError} style={{ marginTop: 8 }} />
            <div className="row gap10" style={{ marginTop: 16 }}>
              <Button
                variant="primary"
                disabled={selected.size === 0 || confirm.isPending}
                onClick={handleConfirm}
              >
                {confirm.isPending
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
