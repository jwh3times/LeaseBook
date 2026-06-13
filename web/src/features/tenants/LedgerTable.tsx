import { useVirtualizer } from '@tanstack/react-virtual';
import { useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { Badge, type BadgeTone, Icon, type IconName, Money } from '@/design';
import { num } from '@/lib/directory';
import type { TenantLedgerEntry } from './ledger';

export interface LedgerTableProps {
  /** Rows in display order (newest first). */
  rows: TenantLedgerEntry[];
  /** The just-posted entry id to flash (driven by the composer/mutation result, P59). */
  flashId?: string | null;
  /**
   * Per-row action menu seam (§C.4). WP-04 ships no actions; WP-06 fills it (Void, Apply…, History).
   * When provided, an Actions column is rendered.
   */
  rowActions?: (entry: TenantLedgerEntry) => ReactNode;
}

const ROW_HEIGHT = 48;

const CATEGORY_TONE: Record<string, BadgeTone> = {
  Rent: 'neutral',
  'Late Fee': 'warn',
  Maintenance: 'neutral',
  Fee: 'warn',
  Payment: 'pos',
  Credit: 'accent',
  'Security Deposit': 'accent',
  Prepayment: 'accent',
};

function statusOf(entry: TenantLedgerEntry): { label: string; tone: BadgeTone; icon: IconName } {
  if (entry.isVoided) return { label: 'Voided', tone: 'neutral', icon: 'x' };
  if (entry.reversesEntryId) return { label: 'Reversal', tone: 'warn', icon: 'refresh' };
  return { label: 'Posted', tone: 'pos', icon: 'check' };
}

/**
 * The ledger table: virtualized for long histories (P59), keyboard-navigable (arrow keys move a roving
 * selection, focus stays on the grid so it survives virtualization), with the running balance, category
 * badges, a status badge (icon + label, never color alone), struck-through voided rows, linked reversal
 * rows, a new-row flash, and the WP-06 row-action seam. Money renders through <Money> (org neg-display).
 */
export function LedgerTable({ rows, flashId, rowActions }: LedgerTableProps) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const [active, setActive] = useState(0);
  const hasActions = Boolean(rowActions);

  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 10,
  });

  const moveTo = (index: number) => {
    const clamped = Math.min(rows.length - 1, Math.max(0, index));
    setActive(clamped);
    virtualizer.scrollToIndex(clamped, { align: 'auto' });
  };

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      moveTo(active + 1);
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      moveTo(active - 1);
    } else if (event.key === 'Home') {
      event.preventDefault();
      moveTo(0);
    } else if (event.key === 'End') {
      event.preventDefault();
      moveTo(rows.length - 1);
    }
  };

  return (
    <div className={`pf-ledger${hasActions ? ' has-actions' : ''}`}>
      <div className="pf-ledger-head" role="row">
        <span role="columnheader">Date</span>
        <span role="columnheader">Type</span>
        <span role="columnheader">Description</span>
        <span role="columnheader" className="num">Charge</span>
        <span role="columnheader" className="num">Payment</span>
        <span role="columnheader" className="num">Balance</span>
        <span role="columnheader">Status</span>
        {hasActions && <span role="columnheader" className="acts">Actions</span>}
      </div>
      <div
        ref={scrollRef}
        className="pf-ledger-body"
        role="grid"
        aria-label="Tenant ledger"
        aria-rowcount={rows.length}
        tabIndex={0}
        onKeyDown={onKeyDown}
      >
        <div style={{ height: virtualizer.getTotalSize(), position: 'relative' }}>
          {virtualizer.getVirtualItems().map((item) => {
            const entry = rows[item.index];
            if (!entry) return null;
            const status = statusOf(entry);
            const charge = num(entry.charge);
            const payment = num(entry.payment);
            const selected = item.index === active;
            return (
              <div
                key={entry.entryId}
                role="row"
                aria-selected={selected || undefined}
                data-entry-id={entry.entryId}
                className={[
                  'pf-ledger-row',
                  entry.isVoided ? 'voided' : '',
                  entry.entryId === flashId ? 'flash' : '',
                  selected ? 'sel' : '',
                ]
                  .filter(Boolean)
                  .join(' ')}
                style={{
                  position: 'absolute',
                  top: 0,
                  left: 0,
                  width: '100%',
                  height: item.size,
                  transform: `translateY(${item.start}px)`,
                }}
                onClick={() => setActive(item.index)}
              >
                <span role="gridcell" className="muted nowrap">
                  {entry.date}
                </span>
                <span role="gridcell">
                  <Badge tone={CATEGORY_TONE[entry.category] ?? 'neutral'} soft>
                    {entry.category}
                  </Badge>
                </span>
                <span role="gridcell" className="desc">
                  {entry.reversesEntryId && <Icon name="refresh" size={12} />}
                  <span>{entry.description || '—'}</span>
                </span>
                <span role="gridcell" className="num">
                  {charge > 0 ? <Money value={charge} plain /> : <span className="t3">—</span>}
                </span>
                <span role="gridcell" className="num">
                  {payment > 0 ? <Money value={payment} plain colorize /> : <span className="t3">—</span>}
                </span>
                <span role="gridcell" className="num strong">
                  <Money value={num(entry.balance)} />
                </span>
                <span role="gridcell">
                  <Badge tone={status.tone} soft icon={status.icon}>
                    {status.label}
                  </Badge>
                </span>
                {hasActions && (
                  <span role="gridcell" className="acts">
                    {rowActions?.(entry)}
                  </span>
                )}
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
}
