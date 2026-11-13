import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useRef, useState } from 'react';
import { Badge, Button, formatMoney, Money } from '@/design';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { QueryErrorState } from '@/components/QueryErrorState';
import { num } from '@/lib/directory';
import {
  type BankingError,
  bankRegisterKey,
  finalizeReconciliation,
  type ReconciliationLine,
  reconciliationHistoryKey,
  reconciliationLinesKey,
  type ReconciliationSummary,
  startReconciliation,
  STATUS,
  statusMeta,
  useReconciliationLines,
} from './banking';

interface FinalizeAgainPanelProps {
  /** The id the row's disclosure button names in `aria-controls`. */
  id: string;
  reconciliation: ReconciliationSummary;
  /** The month as the history row names it, e.g. `Aug 2026`. */
  period: string;
  onClose: () => void;
  onFinalized: () => void;
}

/** Money is summed in whole cents: a float sum of 0.1 + 0.2 is not a zero difference waiting to happen. */
const toCents = (amount: number) => Math.round(amount * 100);

/** The line's clearance status crosses this route as a word (`cleared`/`uncleared`), not a number. */
const lineStatus = (line: ReconciliationLine) =>
  statusMeta(line.status === 'cleared' ? STATUS.cleared : STATUS.uncleared);

/**
 * Finalize a reopened month again (#656, the ADR-014 addendum of 2026-11-09), in place under its
 * history row. The month reconciles to its own statement: the balance its account's reconciliations
 * already stamped through it, plus the lines ticked here, which are the lines that statement shows.
 * The difference shown is the one finalize computes, because finalize takes exactly the ticked ids.
 * Lines of one payout tick together, as they clear together (ADR-053). Finalize is button-only: it
 * locks the month, so a stray Enter in the statement field must not do it (#711).
 */
export function FinalizeAgainPanel({
  id,
  reconciliation,
  period,
  onClose,
  onFinalized,
}: FinalizeAgainPanelProps) {
  const ids = useId();
  const headingRef = useRef<HTMLHeadingElement>(null);
  const queryClient = useQueryClient();
  const lines = useReconciliationLines(reconciliation.id);
  const storedCents = toCents(num(reconciliation.statementEndingBalance));
  const [statement, setStatement] = useState((storedCents / 100).toFixed(2));
  const [ticked, setTicked] = useState<ReadonlySet<string>>(new Set());

  useEffect(() => headingRef.current?.focus(), []);

  const statementNum = Number.parseFloat(statement);
  const statementCents = Number.isFinite(statementNum) ? toCents(statementNum) : null;
  const rows = lines.data?.lines ?? [];
  const tickedCents = rows
    .filter((l) => ticked.has(l.journalLineId))
    .reduce((sum, l) => sum + toCents(num(l.amount)), 0);
  const throughCents = lines.isSuccess ? toCents(num(lines.data.reconciledThrough)) : null;
  const differenceCents =
    statementCents === null || throughCents === null
      ? null
      : statementCents - (throughCents + tickedCents);

  const toggle = (line: ReconciliationLine) => {
    const group = line.payoutReference
      ? rows.filter((l) => l.payoutReference === line.payoutReference)
      : [line];
    const on = !ticked.has(line.journalLineId);
    setTicked((prev) => {
      const next = new Set(prev);
      for (const l of group) {
        if (on) next.add(l.journalLineId);
        else next.delete(l.journalLineId);
      }
      return next;
    });
  };

  const finalize = useMutation<unknown, BankingError, { statementCents: number; ids: string[] }>({
    mutationFn: async ({ statementCents: cents, ids: assigned }) => {
      // A changed statement balance is set the way reconcile-in-place sets it, by re-entering the
      // month, which the audit trail records as a change to the reconciliation.
      if (cents !== storedCents) {
        await startReconciliation({
          bankAccountId: reconciliation.bankAccountId,
          year: num(reconciliation.year),
          month: num(reconciliation.month),
          statementEndingBalance: cents / 100,
        });
      }
      return finalizeReconciliation(reconciliation.id, assigned);
    },
    onSettled: () => {
      // A refusal after the statement was re-entered still changed it, so the history reads again
      // either way. So do the lines: a refusal such as a line another month has since reconciled
      // means the list on screen is stale.
      void queryClient.invalidateQueries({
        queryKey: reconciliationHistoryKey(reconciliation.bankAccountId),
      });
      void queryClient.invalidateQueries({ queryKey: reconciliationLinesKey(reconciliation.id) });
    },
    onSuccess: () => {
      // Finalize cleared the ticked uncleared lines and stamped them, which the register, the
      // balances and the dashboard's uncleared counts all show.
      void queryClient.invalidateQueries({
        queryKey: bankRegisterKey(reconciliation.bankAccountId),
      });
      void queryClient.invalidateQueries({ queryKey: ['bank-balances'] });
      void queryClient.invalidateQueries({ queryKey: ['dashboard'] });
      onFinalized();
    },
  });

  const balanced = differenceCents === 0;
  const canFinalize = balanced && statementCents !== null && !finalize.isPending;
  const title = `Finalize ${period} again`;

  return (
    <section id={id} className="pf-recon-again" aria-label={title}>
      <h4 ref={headingRef} tabIndex={-1} className="fw6">
        {title}
      </h4>
      <p className="t3 fs13">
        Tick the lines the {period} statement shows. Lines reconciled before stay reconciled, and
        ticking an uncleared line clears it.
      </p>

      {lines.isError ? (
        <QueryErrorState
          query={lines}
          title="Couldn't load the lines"
          fallback="Failed to load the lines this month can be reconciled with."
        />
      ) : lines.isPending ? (
        <div className="col gap8">
          {[0, 1, 2].map((row) => (
            <div key={row} className="pf-skeleton" style={{ height: 20 }} />
          ))}
        </div>
      ) : (
        <>
          <div className={`pf-recon-bar pf-card${balanced ? ' done' : ''}`}>
            <div className="pf-balcell">
              <label className="pf-eyebrow" htmlFor={`${ids}-statement`}>
                Statement ending balance
              </label>
              <div className="pf-input money-input" style={{ width: 170 }}>
                <span aria-hidden="true">$</span>
                <input
                  id={`${ids}-statement`}
                  inputMode="decimal"
                  disabled={finalize.isPending}
                  value={statement}
                  onChange={(e) => setStatement(e.target.value.replace(/[^0-9.-]/g, ''))}
                />
              </div>
            </div>
            <div className="pf-balcell">
              <span className="pf-eyebrow">Reconciled through {period}</span>
              <Money value={num(lines.data.reconciledThrough)} />
            </div>
            <div className="pf-balcell">
              <span className="pf-eyebrow">Ticked</span>
              <Money value={tickedCents / 100} sign />
            </div>
            <div className="pf-balcell">
              <span className="pf-eyebrow">Difference</span>
              <span className="pf-money fw6" aria-label="Difference">
                {differenceCents === null
                  ? 'Enter the statement balance'
                  : differenceCents === 0
                    ? // Exactly zero is the goal, not a signed amount: "+$0.00" reads as a remainder.
                      formatMoney(0, { dash: false })
                    : formatMoney(differenceCents / 100, { sign: true })}
              </span>
            </div>
          </div>

          {rows.length === 0 ? (
            <p className="t3 fs13">No unreconciled lines are dated through {period}.</p>
          ) : (
            <div style={{ overflowX: 'auto' }}>
              <table className="pf-table">
                <thead>
                  <tr>
                    <th style={{ width: 44 }} aria-label="Assign" />
                    <th style={{ width: 120 }}>Date</th>
                    <th>Description</th>
                    <th style={{ width: 140 }}>Status</th>
                    <th className="num" style={{ width: 120 }}>
                      Amount
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((line) => {
                    const meta = lineStatus(line);
                    const description = line.description ?? 'Bank line';
                    return (
                      <tr key={line.journalLineId}>
                        <td>
                          <input
                            type="checkbox"
                            className="pf-check"
                            aria-label={`Assign ${description} (${line.entryDate})`}
                            checked={ticked.has(line.journalLineId)}
                            disabled={finalize.isPending}
                            onChange={() => toggle(line)}
                          />
                        </td>
                        <td className="muted" style={{ whiteSpace: 'nowrap' }}>
                          {line.entryDate}
                        </td>
                        <td className="strong">
                          {description}
                          {line.payoutReference && (
                            <div className="t3 fs12">Payout {line.payoutReference}</div>
                          )}
                        </td>
                        <td>
                          <Badge tone={meta.tone} soft dot>
                            {meta.label}
                          </Badge>
                        </td>
                        <td className="num">
                          <Money value={num(line.amount)} />
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}

      <ApiErrorNotice error={finalize.error} />
      <div className="row gap10">
        {!balanced && lines.isSuccess && (
          <span id={`${ids}-why`} className="t3 fs13">
            Finalize is available at a $0.00 difference.
          </span>
        )}
        <div className="ml-auto row gap10">
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="primary"
            icon="check"
            disabled={!canFinalize}
            aria-describedby={!balanced && lines.isSuccess ? `${ids}-why` : undefined}
            onClick={() => {
              if (statementCents === null) return;
              // Only lines still on offer: a re-read after a refusal can drop one that was ticked.
              const assigned = rows
                .filter((l) => ticked.has(l.journalLineId))
                .map((l) => l.journalLineId);
              finalize.mutate({ statementCents, ids: assigned });
            }}
          >
            {finalize.isPending ? 'Finalizing…' : `Finalize ${period}`}
          </Button>
        </div>
      </div>
    </section>
  );
}
