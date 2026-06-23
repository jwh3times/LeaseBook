/**
 * Owner disbursement run screen (M6 WP-5).
 * Per-owner rows: gross equity → management fee → net before reserve → reserve → disburse amount.
 * Selective — operator confirms which owners to disburse. Aligns with the UX budget (≤ 2 clicks).
 */
import { useState } from 'react';
import { Badge, Button, Card, CardHeader, EmptyState, Money } from '@/design';
import { trackInteraction } from '@/lib/telemetry';
import { PeriodPicker } from './PeriodPicker';
import { RunResultPanel } from './RunPreviewGrid';
import { useConfirmRun, useRunPreview } from './useRuns';
import type { PreviewRowSpa, RunResultSpaResponse } from './useRuns';

function currentPeriod() {
  const now = new Date();
  return { year: now.getFullYear(), month: now.getMonth() + 1 };
}

/** Disburse detail keys from the strategy: equity / fee / netBeforeReserve / reserve. */
function DisbursementDetail({ row }: { row: PreviewRowSpa }) {
  const d = row.detail as Record<string, string>;
  if (!d['equity']) return null;
  return (
    <div className="col gap2" style={{ fontSize: 12, color: 'var(--text-3)' }}>
      <span>
        Equity <Money value={Number(d['equity'])} plain /> · Fee{' '}
        <Money value={Number(d['fee'])} plain /> · Reserve{' '}
        <Money value={Number(d['reserve'])} plain />
      </span>
    </div>
  );
}

const EXCLUDED_LABELS: Record<string, string> = {
  non_positive_equity: 'Non-positive equity',
  below_reserve_floor: 'Below reserve',
};

function excludedLabel(reason: string) {
  return EXCLUDED_LABELS[reason] ?? reason.replace(/_/g, ' ');
}

export function DisbursementRunScreen() {
  const [period, setPeriod] = useState(currentPeriod);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [result, setResult] = useState<RunResultSpaResponse | null>(null);
  const [confirmError, setConfirmError] = useState<string | null>(null);

  const preview = useRunPreview('disbursement', period.year, period.month);
  const confirm = useConfirmRun('disbursement');

  const handlePeriodChange = (y: number, m: number) => {
    setPeriod({ year: y, month: m });
    setSelected(new Set());
  };

  const eligibleRows = preview.data?.rows.filter((r) => !r.excludedReason && !r.alreadyDone) ?? [];
  const eligibleIds = eligibleRows.map((r) => r.targetId);

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
    // Disbursement run: entering Operations + clicking Confirm ≤ 2 clicks.
    trackInteraction('disbursement-run-confirm', 2, true);
    confirm.mutate(
      { year: period.year, month: period.month, selectedTargetIds: Array.from(selected) },
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
        onDone={() => {
          setResult(null);
          setSelected(new Set());
        }}
      />
    );
  }

  const allEligibleSelected = eligibleIds.length > 0 && eligibleIds.every((id) => selected.has(id));
  const rows = preview.data?.rows ?? [];

  return (
    <div className="col gap16">
      <Card pad>
        <div className="row" style={{ justifyContent: 'space-between', alignItems: 'center' }}>
          <CardHeader
            title="Owner disbursement run"
            sub="Post management fees and disburse net equity to owners."
          />
          <PeriodPicker year={period.year} month={period.month} onChange={handlePeriodChange} />
        </div>
      </Card>

      <Card pad>
        {preview.isPending ? (
          <div className="col gap8">
            {[0, 1, 2, 3].map((i) => (
              <div key={i} className="pf-skeleton" style={{ height: 28 }} />
            ))}
          </div>
        ) : preview.isError ? (
          <EmptyState
            icon="alert"
            title="Couldn't load preview"
            description="Please retry in a moment."
          />
        ) : rows.length === 0 ? (
          <EmptyState
            icon="doc"
            title="No owners to disburse"
            description="No eligible owners found for this period."
          />
        ) : (
          <>
            <table className="pf-table">
              <thead>
                <tr>
                  <th style={{ width: 36 }}>
                    <input
                      type="checkbox"
                      aria-label="Select all eligible"
                      checked={allEligibleSelected}
                      onChange={handleToggleAll}
                    />
                  </th>
                  <th>Owner</th>
                  <th className="num">Disbursement</th>
                  <th>Status</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((row) => {
                  const isExcluded = !!row.excludedReason;
                  const isAlreadyDone = row.alreadyDone;
                  const isEligible = !isExcluded && !isAlreadyDone;
                  const isSel = selected.has(row.targetId);

                  return (
                    <tr
                      key={row.targetId}
                      className={isExcluded || isAlreadyDone ? 'muted' : undefined}
                      onClick={isEligible ? () => handleToggle(row.targetId) : undefined}
                      onKeyDown={
                        isEligible
                          ? (e) => {
                              if (e.key === ' ' || e.key === 'Enter') {
                                e.preventDefault();
                                handleToggle(row.targetId);
                              }
                            }
                          : undefined
                      }
                      role={isEligible ? 'checkbox' : undefined}
                      aria-checked={isEligible ? isSel : undefined}
                      tabIndex={isEligible ? 0 : undefined}
                      style={isEligible ? { cursor: 'pointer' } : undefined}
                    >
                      <td>
                        {isEligible && (
                          <input
                            type="checkbox"
                            aria-label={`Select ${row.label}`}
                            checked={isSel}
                            onChange={() => handleToggle(row.targetId)}
                            onClick={(e) => e.stopPropagation()}
                          />
                        )}
                      </td>
                      <td>
                        <div className="col gap2">
                          <span className="strong">{row.label}</span>
                          <DisbursementDetail row={row} />
                        </div>
                      </td>
                      <td className="num">
                        {isEligible ? (
                          <Money value={Number(row.amount)} />
                        ) : (
                          <span className="t3">—</span>
                        )}
                      </td>
                      <td>
                        {isExcluded ? (
                          <Badge tone="warn" icon="alert">
                            {excludedLabel(row.excludedReason!)}
                          </Badge>
                        ) : isAlreadyDone ? (
                          <Badge tone="accent" icon="check">
                            Already done
                          </Badge>
                        ) : (
                          <Badge tone="pos" icon="check">
                            Eligible
                          </Badge>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>

            {eligibleIds.length > 0 && (
              <div className="t3 fs12" style={{ marginTop: 8 }}>
                {selected.size} of {eligibleIds.length} owners selected
              </div>
            )}

            {confirmError && (
              <p className="pf-composer-error" role="alert" style={{ marginTop: 8 }}>
                {confirmError}
              </p>
            )}

            <div className="row gap10" style={{ marginTop: 16 }}>
              <Button
                variant="primary"
                disabled={selected.size === 0 || confirm.isPending}
                onClick={handleConfirm}
              >
                {confirm.isPending
                  ? 'Disbursing…'
                  : selected.size === 0
                    ? 'Select owners to disburse'
                    : `Disburse ${selected.size} owner${selected.size === 1 ? '' : 's'}`}
              </Button>
            </div>
          </>
        )}
      </Card>
    </div>
  );
}
