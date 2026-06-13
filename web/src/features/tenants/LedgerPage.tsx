import { useQueryClient } from '@tanstack/react-query';
import { useCallback, useMemo, useState } from 'react';
import { useParams, useSearchParams } from 'react-router-dom';
import { Avatar, Button, Card, EmptyState, Icon, IconButton, Input, Money, Select } from '@/design';
import { num, useTenantDetail } from '@/lib/directory';
import { RecordQuickSwitch } from '@/lib/RecordQuickSwitch';
import { TenantStatusBadge } from '@/lib/StatusBadge';
import { ApplyModal } from './ApplyModal';
import { AuditDrawer } from './AuditDrawer';
import { LedgerComposer } from './LedgerComposer';
import { LedgerTable } from './LedgerTable';
import { VoidDialog } from './VoidDialog';
import { downloadLedgerCsv, type TenantLedgerEntry, tenantLedgerKey, useTenantLedger } from './ledger';

function initialsOf(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  return ((parts[0]?.[0] ?? '') + (parts[1]?.[0] ?? '')).toUpperCase() || '·';
}

function balanceCaption(balance: number): string {
  if (balance > 0) return 'Tenant owes';
  if (balance < 0) return 'Credit on file';
  return 'Paid in full';
}

/**
 * The tenant ledger hub (M3 / screen-ledger). Header (identity/unit/lease/status + balance + deposit
 * held "Liability · not income"), the inline-composer slot (WP-05 fills it), and the ledger table — a
 * virtualized, filterable running-balance view with a CSV export and the WP-06 row-action seam.
 */
export function LedgerPage() {
  const { id = '' } = useParams();
  const [searchParams] = useSearchParams();
  const composeParam = searchParams.get('compose');
  const initialMode = composeParam === 'payment' || composeParam === 'charge' ? composeParam : undefined;

  const queryClient = useQueryClient();
  const detail = useTenantDetail(id);
  const ledger = useTenantLedger(id);

  const [flashId, setFlashId] = useState<string | null>(null);
  const [typeFilter, setTypeFilter] = useState('all');
  const [fromDate, setFromDate] = useState('');
  const [toDate, setToDate] = useState('');

  // WP-06 row-action targets: which entry to void / show history for, and which held funds to apply.
  const [voidEntryId, setVoidEntryId] = useState<string | null>(null);
  const [auditEntryId, setAuditEntryId] = useState<string | null>(null);
  const [applyKind, setApplyKind] = useState<'deposit' | 'prepayment' | null>(null);

  // A successful post refetches the ledger (and the header balance/deposit) and flashes the new row —
  // the "appears without navigation" contract (P59).
  const handlePosted = useCallback(
    (entryId: string) => {
      void queryClient.invalidateQueries({ queryKey: tenantLedgerKey(id) });
      void queryClient.invalidateQueries({ queryKey: ['tenant', id] });
      setFlashId(entryId);
      window.setTimeout(() => setFlashId((current) => (current === entryId ? null : current)), 1600);
    },
    [id, queryClient],
  );

  const allRows = useMemo(() => ledger.data?.rows ?? [], [ledger.data]);
  const categories = useMemo(
    () => ['all', ...Array.from(new Set(allRows.map((row) => row.category)))],
    [allRows],
  );

  // Client-side filter over the loaded ledger (P42), then newest-first for display (each row carries
  // its own running balance, so reversing the order keeps every balance correct).
  const display = useMemo(() => {
    const filtered = allRows.filter((row) => {
      if (typeFilter !== 'all' && row.category !== typeFilter) return false;
      if (fromDate && row.date < fromDate) return false;
      if (toDate && row.date > toDate) return false;
      return true;
    });
    return filtered.reverse();
  }, [allRows, typeFilter, fromDate, toDate]);

  const filtersActive = typeFilter !== 'all' || fromDate !== '' || toDate !== '';
  const clearFilters = () => {
    setTypeFilter('all');
    setFromDate('');
    setToDate('');
  };

  const onExport = () => {
    void downloadLedgerCsv(id);
  };

  // The row-action menu (fills WP-04's seam): void on posted not-yet-reversed rows, history on every
  // row. Applying held funds is a tenant-level action (deposits aren't ledger rows) → the header button.
  const rowActions = (entry: TenantLedgerEntry) => (
    <div className="row gap2">
      {!entry.isVoided && !entry.reversesEntryId && (
        <IconButton name="x" label="Void entry" onClick={() => setVoidEntryId(entry.entryId)} />
      )}
      <IconButton name="clock" label="History" onClick={() => setAuditEntryId(entry.entryId)} />
    </div>
  );

  return (
    <div className="pf-fade">
      <div className="row gap8 mb16">
        <RecordQuickSwitch kind="tenants" currentId={id} toPath={(tenantId) => `/tenants/${tenantId}`} />
      </div>

      {/* Header */}
      {detail.isPending ? (
        <Card pad>
          <div className="pf-skeleton" style={{ maxWidth: 280, height: 26 }} />
        </Card>
      ) : detail.isError || !detail.data ? (
        <Card pad>
          <EmptyState icon="alert" title="Tenant not found" description="It may have been removed, or the link is wrong." />
        </Card>
      ) : (
        <Card className="pf-tenant-hd">
          <div className="pf-pad row gap16" style={{ flexWrap: 'wrap' }}>
            <Avatar initials={initialsOf(detail.data.displayName)} size={54} />
            <div className="col gap4" style={{ flex: 1, minWidth: 200 }}>
              <div className="row gap10">
                <h2 style={{ margin: 0, fontSize: 22, letterSpacing: '-.02em', fontWeight: 750 }}>
                  {detail.data.displayName}
                </h2>
                <TenantStatusBadge status={detail.data.status} />
              </div>
              <div className="row gap12 t3 fs13" style={{ flexWrap: 'wrap' }}>
                <span className="row gap6">
                  <Icon name="building" size={14} />
                  {detail.data.unitLabel ?? 'No unit'}
                </span>
                {detail.data.lease && (
                  <span className="row gap6">
                    <Icon name="clock" size={14} />
                    Lease {detail.data.lease.startDate ?? '—'} – {detail.data.lease.endDate ?? '—'}
                  </span>
                )}
                {detail.data.contact.phone && <span>{detail.data.contact.phone}</span>}
              </div>
            </div>
            <div className="pf-tenant-stats">
              <div className="pf-tstat">
                <span className="pf-eyebrow">Current balance</span>
                <Money value={num(detail.data.balance)} big colorize />
                <span className="t3 fs12">{balanceCaption(num(detail.data.balance))}</span>
              </div>
              <div className="pf-tstat-div" />
              <div className="pf-tstat">
                <span className="pf-eyebrow">Deposit held</span>
                <span style={{ fontSize: '1.5em', fontWeight: 720 }}>
                  <Money value={num(detail.data.depositHeld)} plain />
                </span>
                <span className="fs12" style={{ color: 'var(--accent-strong)' }}>
                  Liability · not income
                </span>
                <Button variant="ghost" size="sm" icon="arrowUpRight" onClick={() => setApplyKind('deposit')}>
                  Apply…
                </Button>
              </div>
            </div>
          </div>
        </Card>
      )}

      {/* Composer slot (WP-05 fills it). */}
      <LedgerComposer tenantId={id} onPosted={handlePosted} initialMode={initialMode} />

      {/* Ledger */}
      <Card className="pf-ledger-card">
        <div className="pf-card-hd">
          <div>
            <h3>Ledger</h3>
            <div className="sub">{display.length} entries · running balance</div>
          </div>
          <div className="pf-ledger-filters">
            <Select value={typeFilter} onChange={(e) => setTypeFilter(e.target.value)} aria-label="Filter by type">
              {categories.map((category) => (
                <option key={category} value={category}>
                  {category === 'all' ? 'All types' : category}
                </option>
              ))}
            </Select>
            <Input type="date" value={fromDate} onChange={(e) => setFromDate(e.target.value)} aria-label="From date" />
            <Input type="date" value={toDate} onChange={(e) => setToDate(e.target.value)} aria-label="To date" />
            {filtersActive && (
              <Button variant="ghost" size="sm" onClick={clearFilters}>
                Clear
              </Button>
            )}
            <Button icon="download" variant="ghost" size="sm" onClick={onExport}>
              Export
            </Button>
          </div>
        </div>

        {ledger.isPending ? (
          <div className="pf-pad col gap8">
            {[0, 1, 2, 3].map((row) => (
              <div key={row} className="pf-skeleton" style={{ height: 20 }} />
            ))}
          </div>
        ) : ledger.isError ? (
          <div className="pf-pad">
            <EmptyState icon="alert" title="Couldn't load the ledger" description="Please retry in a moment." />
          </div>
        ) : allRows.length === 0 ? (
          <div className="pf-pad">
            <EmptyState icon="doc" title="No ledger activity yet" description="Charges and payments will appear here." />
          </div>
        ) : display.length === 0 ? (
          <div className="pf-pad">
            <EmptyState icon="search" title="No entries match the filter" description="Adjust the type or date range." />
          </div>
        ) : (
          <LedgerTable rows={display} flashId={flashId} rowActions={rowActions} />
        )}
      </Card>

      {voidEntryId && (
        <VoidDialog
          entryId={voidEntryId}
          onClose={() => setVoidEntryId(null)}
          onVoided={(reversalId) => {
            setVoidEntryId(null);
            handlePosted(reversalId);
          }}
        />
      )}
      {applyKind && (
        <ApplyModal
          tenantId={id}
          initialKind={applyKind}
          onClose={() => setApplyKind(null)}
          onApplied={(entryId) => {
            setApplyKind(null);
            handlePosted(entryId);
          }}
        />
      )}
      {auditEntryId && <AuditDrawer entryId={auditEntryId} onClose={() => setAuditEntryId(null)} />}
    </div>
  );
}
