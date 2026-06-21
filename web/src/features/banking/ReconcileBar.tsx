import { Button, Icon, Money } from '@/design';

interface ReconcileBarProps {
  statementBalance: string;
  onStatementBalanceChange: (value: string) => void;
  /** Cleared/reconciled balance on the account (the base the ticked items add to). */
  clearedBalance: number;
  /** Signed sum of the currently-ticked uncleared items. */
  selectedSum: number;
  reconciled: boolean;
  onSelectAll: () => void;
  onFinalize: () => void;
  finalizing: boolean;
}

/**
 * The reconcile-in-place control bar (P64 / screen-bank): statement ending balance → cleared + selected →
 * difference, driven to <b>$0.00</b>. Finalize is enabled only at a zero difference (the green state); the
 * server re-checks regardless. Mirrors the prototype's `pf-recon-bar`.
 */
export function ReconcileBar({
  statementBalance,
  onStatementBalanceChange,
  clearedBalance,
  selectedSum,
  reconciled,
  onSelectAll,
  onFinalize,
  finalizing,
}: ReconcileBarProps) {
  const current = clearedBalance + selectedSum;
  const statementNum = Number.parseFloat(statementBalance);
  const difference = (Number.isFinite(statementNum) ? statementNum : 0) - current;

  return (
    <div className={`pf-recon-bar pf-card${reconciled ? ' done' : ''}`}>
      <div className="pf-balcell">
        <span className="pf-eyebrow">Statement ending balance</span>
        <div className="pf-input money-input" style={{ width: 170 }}>
          <span aria-hidden="true">$</span>
          <input
            inputMode="decimal"
            aria-label="Statement ending balance"
            value={statementBalance}
            onChange={(e) => onStatementBalanceChange(e.target.value.replace(/[^0-9.-]/g, ''))}
          />
        </div>
      </div>

      <Icon name="chevronRight" size={18} style={{ color: 'var(--text-3)' }} />

      <div className="pf-balcell">
        <span className="pf-eyebrow">Cleared + selected</span>
        <span className="pf-money" style={{ fontSize: '1.35em', fontWeight: 700 }}>
          <Money value={current} plain />
        </span>
      </div>

      <div className="pf-recon-eq">=</div>

      <div className="pf-balcell">
        <span className="pf-eyebrow">Difference</span>
        <span
          className="pf-money"
          aria-label="Difference"
          style={{
            fontSize: '1.5em',
            fontWeight: 750,
            color: reconciled ? 'var(--pos)' : 'var(--neg)',
          }}
        >
          {reconciled ? '$0.00' : <Money value={difference} sign />}
        </span>
      </div>

      <div className="ml-auto row gap10">
        {reconciled ? (
          <>
            <div className="pf-recon-check">
              <Icon name="check" size={16} />
              <span>
                <b>Reconciled.</b> Ready to finalize.
              </span>
            </div>
            <Button
              variant="primary"
              size="md"
              icon="check"
              disabled={finalizing}
              onClick={onFinalize}
            >
              Finalize
            </Button>
          </>
        ) : (
          <Button variant="primary" size="md" onClick={onSelectAll} icon="check">
            Select all uncleared
          </Button>
        )}
      </div>
    </div>
  );
}
