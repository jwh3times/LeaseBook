import { Badge, Icon, Money } from '@/design';
import { num } from '@/lib/directory';
import { type RegisterRow, STATUS, statusMeta } from './banking';

interface RegisterTableProps {
  rows: RegisterRow[];
  /** Optional resolver of a property id → label (the register read returns ids only). */
  propertyLabel?: (propertyId: string | null) => string;
  reconciling: boolean;
  selected: Record<string, boolean>;
  onToggle: (row: RegisterRow) => void;
  onToggleAll: () => void;
}

/**
 * The bank register table (P69): each journal line on the account as a statement-style row — deposit
 * (debit) / withdrawal (credit) / clearance status. In reconcile mode a checkbox column lets the user tick
 * uncleared rows; already-cleared/reconciled rows show a check glyph instead. Status is a labelled badge
 * (icon + word), never color alone (CLAUDE.md).
 */
export function RegisterTable({
  rows,
  propertyLabel,
  reconciling,
  selected,
  onToggle,
  onToggleAll,
}: RegisterTableProps) {
  const uncleared = rows.filter((r) => r.status === STATUS.uncleared);
  const allTicked = uncleared.length > 0 && uncleared.every((r) => selected[r.journalLineId]);

  return (
    <div style={{ overflowX: 'auto' }}>
      <table className="pf-table">
        <thead>
          <tr>
            {reconciling && (
              <th style={{ width: 44 }}>
                <input
                  type="checkbox"
                  className="pf-check"
                  aria-label="Select all uncleared"
                  checked={allTicked}
                  onChange={onToggleAll}
                />
              </th>
            )}
            <th style={{ width: 120 }}>Date</th>
            <th>Description</th>
            <th>Property</th>
            <th className="num" style={{ width: 120 }}>
              Deposit
            </th>
            <th className="num" style={{ width: 120 }}>
              Withdrawal
            </th>
            <th style={{ width: 140 }}>Status</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => {
            const isUncleared = row.status === STATUS.uncleared;
            const ticked = !!selected[row.journalLineId];
            const meta = statusMeta(row.status);
            const clickable = reconciling && isUncleared;
            return (
              <tr
                key={row.journalLineId}
                className={clickable ? 't-row-click' : undefined}
                onClick={clickable ? () => onToggle(row) : undefined}
                style={reconciling && ticked ? { background: 'var(--accent-soft)' } : undefined}
              >
                {reconciling && (
                  <td>
                    {isUncleared ? (
                      // Interactive (not readOnly) so the box is keyboard-operable (Space toggles); the row
                      // onClick stays for mouse convenience, and stopPropagation here prevents a double-toggle.
                      <input
                        type="checkbox"
                        className="pf-check"
                        aria-label={`Clear ${row.description ?? 'transaction'}`}
                        checked={ticked}
                        onChange={() => onToggle(row)}
                        onClick={(e) => e.stopPropagation()}
                      />
                    ) : (
                      <Icon name="check" size={15} style={{ color: 'var(--text-3)' }} />
                    )}
                  </td>
                )}
                <td className="muted" style={{ whiteSpace: 'nowrap' }}>
                  {row.date}
                </td>
                <td className="strong">{row.description ?? '—'}</td>
                <td className="muted">{propertyLabel?.(row.propertyId ?? null) ?? '—'}</td>
                <td className="num">
                  {row.deposit != null ? (
                    <Money value={num(row.deposit)} plain />
                  ) : (
                    <span className="t3">—</span>
                  )}
                </td>
                <td className="num">
                  {row.withdrawal != null ? (
                    <Money value={num(row.withdrawal)} plain />
                  ) : (
                    <span className="t3">—</span>
                  )}
                </td>
                <td>
                  <Badge tone={meta.tone} soft dot>
                    {meta.label}
                  </Badge>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
