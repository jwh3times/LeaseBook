import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import type { ApiError } from '@/api';
import { Button, Card, EmptyState, FilterChip, Money } from '@/design';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { QueryErrorState } from '@/components/QueryErrorState';
import { RefundCheckStatusBadge } from '@/components/StatusBadge';
import { num } from '@/lib/directory';
import { CheckPrintSettingsDialog } from './CheckPrintSettingsDialog';
import { VoidRefundCheckDialog } from './VoidRefundCheckDialog';
import {
  invalidateAfterRefundCheck,
  printRefundCheck,
  REFUND_CHECK_PAGE_SIZE,
  type RefundCheckFilter,
  type RefundCheckView,
  refundChecksKey,
  sourceLabel,
  useRefundChecks,
} from './refundChecks';
import './refundChecks.css';

export interface RefundChecksPanelProps {
  bankAccountId: string;
  bankName: string;
}

function countLabel(filter: RefundCheckFilter, total: number): string {
  return filter === 'outstanding'
    ? `${total} outstanding`
    : `${total} check${total === 1 ? '' : 's'}`;
}

/** "51–100 of 120": which rows of the whole list this page holds. */
function rangeLabel(page: number, total: number): string {
  const first = (page - 1) * REFUND_CHECK_PAGE_SIZE + 1;
  return `${first}–${Math.min(page * REFUND_CHECK_PAGE_SIZE, total)} of ${total}`;
}

/** Why a row action is unavailable, in words — the disabled state alone says nothing. */
function voidBlockedReason(status: string): string | null {
  if (status === 'voided') return 'Already voided.';
  if (status === 'cleared' || status === 'reconciled') return 'Cleared the bank — can’t be voided.';
  return null;
}

/**
 * The refund checks drawn on one bank account (#473), newest first: number, date, payee, amount and
 * a derived status (Outstanding until the bank line clears; Voided once reversed). Print re-renders
 * the same number; void is the misprint and stop-payment path. Print offsets for the account's stock
 * live behind "Print settings". The list is paged on the server and can be narrowed there to the
 * outstanding checks, so an old uncleared check is never out of reach (#476).
 */
export function RefundChecksPanel({ bankAccountId, bankName }: RefundChecksPanelProps) {
  const queryClient = useQueryClient();
  const [filter, setFilter] = useState<RefundCheckFilter>('all');
  const [page, setPage] = useState(1);
  const checks = useRefundChecks(bankAccountId, { status: filter, page });
  // While another page or filter loads, the previous one stays on screen (so the pager keeps keyboard
  // focus) but is not an answer for what was asked: no count, no paging, no empty state from it.
  const loading = checks.isPlaceholderData;
  // From the rows on screen; null until a read succeeds.
  const lastPage = checks.isSuccess
    ? Math.max(1, Math.ceil(num(checks.data.total) / REFUND_CHECK_PAGE_SIZE))
    : null;
  const outOfRange = checks.isSuccess && !loading && lastPage !== null && page > lastPage;

  // A void can empty the last page of the outstanding list; step back rather than claim there are none.
  useEffect(() => {
    if (outOfRange && lastPage !== null) setPage(lastPage);
  }, [outOfRange, lastPage]);

  const canGoBack = !loading && page > 1;
  const canGoForward = !loading && lastPage !== null && page < lastPage;

  const showFilter = (next: RefundCheckFilter) => {
    setFilter(next);
    // Page 3 of every check is not page 3 of the outstanding ones.
    setPage(1);
  };
  const [voiding, setVoiding] = useState<RefundCheckView | null>(null);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [printingId, setPrintingId] = useState<string | null>(null);
  const [printError, setPrintError] = useState<ApiError | null>(null);

  const print = async (check: RefundCheckView) => {
    // Guarded here rather than by `disabled`: disabling the focused button would drop focus.
    if (printingId) return;
    setPrintingId(check.id);
    setPrintError(null);
    try {
      await printRefundCheck(check);
    } catch (err) {
      setPrintError(err as ApiError);
    } finally {
      setPrintingId(null);
      // The print count moved (or the check turned out to be voided meanwhile).
      void queryClient.invalidateQueries({ queryKey: refundChecksKey({ bankAccountId }) });
    }
  };

  return (
    <Card className="pf-ledger-card">
      <div className="pf-card-hd">
        <div>
          <h3>Refund checks</h3>
          <div className="sub">
            {checks.isSuccess && !loading
              ? `${countLabel(filter, num(checks.data.total))} · ${bankName}`
              : checks.isError
                ? 'Count unavailable'
                : 'Counting…'}
          </div>
        </div>
        <div className="pf-refund-tools">
          <div role="group" aria-label="Show" className="pf-refund-filter">
            <FilterChip active={filter === 'all'} onClick={() => showFilter('all')}>
              All
            </FilterChip>
            <FilterChip active={filter === 'outstanding'} onClick={() => showFilter('outstanding')}>
              Outstanding
            </FilterChip>
          </div>
          <Button variant="ghost" size="sm" icon="settings" onClick={() => setSettingsOpen(true)}>
            Print settings
          </Button>
        </div>
      </div>

      {checks.isError ? (
        <div className="pf-pad">
          <QueryErrorState
            query={checks}
            title="Couldn't load refund checks"
            fallback="Failed to load the refund checks for this account."
          />
        </div>
      ) : checks.isPending || outOfRange || (loading && checks.data.items.length === 0) ? (
        <div className="pf-pad col gap8">
          {[0, 1, 2].map((row) => (
            <div key={row} className="pf-skeleton" style={{ height: 20 }} />
          ))}
        </div>
      ) : checks.data.items.length === 0 ? (
        <div className="pf-pad">
          {filter === 'outstanding' ? (
            <EmptyState
              icon="doc"
              title="No outstanding refund checks on this account"
              description="Every refund check on this account has cleared the bank or was voided."
            />
          ) : (
            <EmptyState
              icon="doc"
              title="No refund checks on this account"
              description="Refund a held deposit or prepaid credit from a tenant's ledger and the check appears here."
            />
          )}
        </div>
      ) : (
        <>
          {printError && (
            <div className="pf-pad">
              <ApiErrorNotice error={printError} fallback="Couldn’t print the check." kind="read" />
            </div>
          )}
          <div style={{ overflowX: 'auto' }} aria-busy={loading || undefined}>
            <table className="pf-table" aria-label={`Refund checks on ${bankName}`}>
              <thead>
                <tr>
                  <th className="num" style={{ width: 90 }}>
                    Check #
                  </th>
                  <th style={{ width: 120 }}>Issued</th>
                  <th>Payee</th>
                  <th className="num" style={{ width: 120 }}>
                    Amount
                  </th>
                  <th style={{ width: 140 }}>Status</th>
                  <th className="num" style={{ width: 80 }}>
                    Printed
                  </th>
                  <th style={{ width: 200 }}>Actions</th>
                </tr>
              </thead>
              <tbody>
                {checks.data.items.map((check) => {
                  const number = num(check.checkNumber);
                  const printed = num(check.printCount);
                  const voidBlocked = voidBlockedReason(check.status);
                  const isVoided = check.status === 'voided';
                  const reasonId = `refund-check-${check.id}-why`;
                  return (
                    <tr key={check.id}>
                      <td className="num">{number}</td>
                      <td>{check.issueDate}</td>
                      <td>
                        <div className="col">
                          <span>{check.payeeName}</span>
                          <span className="t3 fs12">{sourceLabel(check.source)}</span>
                        </div>
                      </td>
                      <td className="num">
                        <Money value={num(check.amount)} plain />
                      </td>
                      <td>
                        <RefundCheckStatusBadge status={check.status} />
                      </td>
                      <td className="num">{printed === 0 ? 'Not yet' : `${printed}×`}</td>
                      <td>
                        <div className="pf-refund-actions">
                          <Button
                            variant="ghost"
                            size="sm"
                            icon="download"
                            aria-label={`${printed === 0 ? 'Print' : 'Reprint'} check #${number}`}
                            aria-describedby={isVoided ? reasonId : undefined}
                            disabled={isVoided}
                            aria-busy={printingId === check.id || undefined}
                            onClick={() => void print(check)}
                          >
                            {printingId === check.id
                              ? 'Preparing…'
                              : printed === 0
                                ? 'Print'
                                : 'Reprint'}
                          </Button>
                          <Button
                            variant="ghost"
                            size="sm"
                            icon="x"
                            aria-label={`Void check #${number}`}
                            aria-describedby={voidBlocked ? reasonId : undefined}
                            disabled={voidBlocked !== null}
                            onClick={() => setVoiding(check)}
                          >
                            Void
                          </Button>
                        </div>
                        {voidBlocked && (
                          <span id={reasonId} className="pf-refund-why">
                            {isVoided ? 'Voided — can’t be printed or voided again.' : voidBlocked}
                          </span>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          {lastPage !== null && lastPage > 1 && (
            <div className="pf-refund-pager">
              <Button
                variant="ghost"
                size="sm"
                aria-label="Previous page"
                // aria-disabled, not disabled: disabling the focused button would drop focus.
                aria-disabled={!canGoBack}
                onClick={() => {
                  if (canGoBack) setPage((p) => p - 1);
                }}
              >
                Previous
              </Button>
              <span className="t3 fs12">
                {rangeLabel(num(checks.data.page), num(checks.data.total))}
              </span>
              <Button
                variant="ghost"
                size="sm"
                aria-label="Next page"
                aria-disabled={!canGoForward}
                onClick={() => {
                  if (canGoForward) setPage((p) => p + 1);
                }}
              >
                Next
              </Button>
            </div>
          )}
        </>
      )}

      {voiding && (
        <VoidRefundCheckDialog
          check={voiding}
          onClose={() => setVoiding(null)}
          onStale={() =>
            invalidateAfterRefundCheck(queryClient, {
              tenantId: voiding.tenantId,
              bankAccountId: voiding.bankAccountId,
            })
          }
          onVoided={(view) => {
            setVoiding(null);
            invalidateAfterRefundCheck(queryClient, {
              tenantId: view.tenantId,
              bankAccountId: view.bankAccountId,
            });
          }}
        />
      )}
      {settingsOpen && (
        <CheckPrintSettingsDialog
          bankAccountId={bankAccountId}
          bankName={bankName}
          onClose={() => setSettingsOpen(false)}
        />
      )}
    </Card>
  );
}
