/**
 * Shared preview grid for all three run types. Shows per-target rows with amount (tabular
 * numerals via the Money primitive), a status icon + text label (never color alone), and the
 * exceptions list.
 *
 * Status is communicated with both an icon AND a text label per the UX contract (CLAUDE.md):
 *   • Already done  → clock icon + "Already done"
 *   • Excluded      → x-circle icon + excluded reason
 *   • Eligible      → checkbox (check/empty) when selectable
 */
import { Badge, Button, EmptyState, Money } from '@/design';
import type { PreviewRowSpa } from './useRuns';

export interface RunPreviewGridProps {
  rows: PreviewRowSpa[];
  exceptions: string[];
  /** Ids currently selected for confirmation (for selectable runs like LateFee). */
  selected: Set<string>;
  /** Whether rows can be individually selected (Rent = all-or-nothing; LateFee/Disbursement = selective). */
  selectable: boolean;
  onToggle?: (targetId: string) => void;
  onToggleAll?: () => void;
}

const EXCLUDED_LABELS: Record<string, string> = {
  non_positive_equity: 'Non-positive equity',
  below_reserve_floor: 'Below reserve floor',
  owner_not_found: 'Owner not found',
  no_active_lease: 'No active lease',
  zero_rent: 'Zero rent amount',
};

function excludedLabel(reason: string): string {
  return EXCLUDED_LABELS[reason] ?? reason.replace(/_/g, ' ');
}

export function RunPreviewGrid({
  rows,
  exceptions,
  selected,
  selectable,
  onToggle,
  onToggleAll,
}: RunPreviewGridProps) {
  if (rows.length === 0 && exceptions.length === 0) {
    return (
      <EmptyState
        icon="doc"
        title="Nothing to preview"
        description="No eligible targets found for this period."
      />
    );
  }

  const eligibleRows = rows.filter((r) => !r.excludedReason && !r.alreadyDone);
  const allEligibleSelected =
    eligibleRows.length > 0 && eligibleRows.every((r) => selected.has(r.targetId));

  return (
    <div className="col gap12">
      {exceptions.length > 0 && (
        <div className="pf-run-exceptions" role="alert">
          <p className="fw6 fs13">Warnings</p>
          <ul className="pf-run-exc-list">
            {exceptions.map((msg, i) => (
              <li key={i} className="fs13 t3">
                {msg}
              </li>
            ))}
          </ul>
        </div>
      )}

      <table className="pf-table">
        <thead>
          <tr>
            {selectable && (
              <th style={{ width: 36 }}>
                <input
                  type="checkbox"
                  aria-label="Select all eligible"
                  checked={allEligibleSelected}
                  onChange={onToggleAll}
                />
              </th>
            )}
            <th>Target</th>
            <th className="num">Amount</th>
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
                onClick={
                  selectable && isEligible && onToggle ? () => onToggle(row.targetId) : undefined
                }
                onKeyDown={
                  selectable && isEligible && onToggle
                    ? (e) => {
                        if (e.key === ' ' || e.key === 'Enter') {
                          e.preventDefault();
                          onToggle(row.targetId);
                        }
                      }
                    : undefined
                }
                role={selectable && isEligible ? 'checkbox' : undefined}
                aria-checked={selectable && isEligible ? isSel : undefined}
                tabIndex={selectable && isEligible ? 0 : undefined}
                style={selectable && isEligible ? { cursor: 'pointer' } : undefined}
              >
                {selectable && (
                  <td>
                    {isEligible ? (
                      <input
                        type="checkbox"
                        aria-label={`Select ${row.label}`}
                        checked={isSel}
                        onChange={onToggle ? () => onToggle(row.targetId) : undefined}
                        onClick={(e) => e.stopPropagation()}
                      />
                    ) : null}
                  </td>
                )}
                <td className="strong">{row.label}</td>
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

      {selectable && eligibleRows.length > 0 && (
        <div className="t3 fs12">
          {selected.size} of {eligibleRows.length} selected
        </div>
      )}
    </div>
  );
}

/** Small confirm-result panel shown after a successful run confirmation. */
export function RunResultPanel({
  posted,
  skipped,
  excluded,
  total,
  onDone,
}: {
  posted: number;
  skipped: number;
  excluded: number;
  total: number;
  onDone: () => void;
}) {
  return (
    <div className="pf-run-result col gap16 pf-fade">
      <div className="col gap8">
        <p className="pf-section-title">Run complete</p>
        <div className="row gap16">
          <span>
            <span className="fw6">{posted}</span>
            <span className="t3 fs13"> posted</span>
          </span>
          {skipped > 0 && (
            <span>
              <span className="fw6">{skipped}</span>
              <span className="t3 fs13"> skipped</span>
            </span>
          )}
          {excluded > 0 && (
            <span>
              <span className="fw6">{excluded}</span>
              <span className="t3 fs13"> excluded</span>
            </span>
          )}
          <span>
            <Money value={total} />
            <span className="t3 fs13"> total</span>
          </span>
        </div>
      </div>
      <Button variant="default" onClick={onDone}>
        Back to Operations
      </Button>
    </div>
  );
}
