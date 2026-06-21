import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';
import { Button, Card, EmptyState, formatMoneyK, Icon, Money, Select } from '@/design';
import { num, useProperties } from '@/lib/directory';
import { trackInteraction } from '@/lib/telemetry';
import { ImportWizard } from './ImportWizard';
import { ReconcileBar } from './ReconcileBar';
import { ReconciliationHistory } from './ReconciliationHistory';
import { RegisterTable } from './RegisterTable';
import {
  applyClearances,
  type BankingError,
  bankRegisterKey,
  finalizeReconciliation,
  reconciliationHistoryKey,
  type RegisterRow,
  rowAmount,
  startReconciliation,
  STATUS,
  useBankBalances,
  useBankRegister,
} from './banking';
import './banking.css';

export function BankingPage() {
  const queryClient = useQueryClient();
  const balances = useBankBalances();
  const properties = useProperties();

  const [acctId, setAcctId] = useState('');
  const [reconciling, setReconciling] = useState(false);
  const [selected, setSelected] = useState<Record<string, boolean>>({});
  const [statementBalance, setStatementBalance] = useState('');
  const [reconcileError, setReconcileError] = useState<string | null>(null);
  const [importing, setImporting] = useState(false);

  const [search, setSearch] = useState('');
  const [propFilter, setPropFilter] = useState('all');
  const [typeFilter, setTypeFilter] = useState('all');

  // Default to the first account once balances load.
  useEffect(() => {
    const first = balances.data?.[0];
    if (acctId === '' && first) {
      setAcctId(first.bankAccountId);
    }
  }, [balances.data, acctId]);

  const register = useBankRegister(acctId);
  const rows = useMemo(() => register.data?.rows ?? [], [register.data]);
  const totals = register.data?.totals;

  const propertyLabel = (id: string | null) =>
    id ? (properties.data?.items.find((p) => p.id === id)?.address ?? '—') : '—';

  const propertyOptions = useMemo(() => {
    const ids = Array.from(
      new Set(rows.map((r) => r.propertyId).filter((id): id is string => !!id)),
    );
    return ids.map((id) => ({ id, label: propertyLabel(id) }));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rows, properties.data]);

  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase();
    return rows.filter((row) => {
      if (propFilter !== 'all' && row.propertyId !== propFilter) return false;
      if (typeFilter === 'deposits' && row.deposit == null) return false;
      if (typeFilter === 'withdrawals' && row.withdrawal == null) return false;
      if (q && !(row.description ?? '').toLowerCase().includes(q)) return false;
      return true;
    });
  }, [rows, search, propFilter, typeFilter]);

  // Reconcile shows every row (so cleared items are visible/ticked); the normal view honors the filters.
  const display = reconciling ? rows : filtered;

  const uncleared = useMemo(() => rows.filter((r) => r.status === STATUS.uncleared), [rows]);
  const selectedSum = uncleared
    .filter((r) => selected[r.journalLineId])
    .reduce((sum, r) => sum + rowAmount(r), 0);
  const clearedBalance = num(totals?.cleared ?? 0);
  const book = num(totals?.book ?? 0);
  const statementNum = Number.parseFloat(statementBalance);
  const reconciled =
    Math.abs((Number.isFinite(statementNum) ? statementNum : 0) - (clearedBalance + selectedSum)) <
    0.005;

  const enterReconcile = () => {
    setReconciling(true);
    setSelected({});
    setStatementBalance(book.toFixed(2));
    setReconcileError(null);
    // Entering reconcile-in-place is one click (CLAUDE.md: start reconciliation ≤ 2 clicks).
    trackInteraction('start-reconcile', 1, true);
  };

  const exitReconcile = () => {
    setReconciling(false);
    setSelected({});
    setReconcileError(null);
  };

  const toggle = (row: RegisterRow) =>
    setSelected((current) => ({ ...current, [row.journalLineId]: !current[row.journalLineId] }));

  const toggleAll = () => {
    const allTicked = uncleared.length > 0 && uncleared.every((r) => selected[r.journalLineId]);
    if (allTicked) {
      setSelected({});
    } else {
      const next: Record<string, boolean> = {};
      uncleared.forEach((r) => (next[r.journalLineId] = true));
      setSelected(next);
    }
  };

  const refreshAccount = () => {
    void queryClient.invalidateQueries({ queryKey: bankRegisterKey(acctId) });
    void queryClient.invalidateQueries({ queryKey: ['bank-balances'] });
    void queryClient.invalidateQueries({ queryKey: reconciliationHistoryKey(acctId) });
  };

  const finalize = useMutation<void, BankingError>({
    mutationFn: async () => {
      const ids = uncleared.filter((r) => selected[r.journalLineId]).map((r) => r.journalLineId);
      if (ids.length > 0) await applyClearances(ids);
      const now = new Date();
      const recon = await startReconciliation({
        bankAccountId: acctId,
        year: now.getFullYear(),
        month: now.getMonth() + 1,
        statementEndingBalance: Number.parseFloat(statementBalance),
      });
      await finalizeReconciliation(recon.id);
    },
    onSuccess: () => {
      refreshAccount();
      exitReconcile();
    },
    onError: (err) => setReconcileError(err.message),
  });

  const inView = display.reduce((sum, r) => sum + (r.deposit != null ? rowAmount(r) : 0), 0);
  const outView = display.reduce((sum, r) => sum + (r.withdrawal != null ? rowAmount(r) : 0), 0);

  if (balances.isPending) {
    return <BankingSkeleton />;
  }
  if (balances.isError || !balances.data || balances.data.length === 0) {
    return (
      <div className="pf-fade">
        <Card pad>
          <EmptyState
            icon={balances.isError ? 'alert' : 'bank'}
            title={balances.isError ? "Couldn't load bank accounts" : 'No bank accounts yet'}
            description={
              balances.isError
                ? 'Please retry in a moment.'
                : 'Add a trust account in Settings to start banking.'
            }
          />
        </Card>
      </div>
    );
  }

  return (
    <div className="pf-fade">
      <div className="pf-pagehd">
        <div>
          <h2>Banking</h2>
          <p>
            One screen mirrors the bank statement — deposits, withdrawals, clear &amp; reconcile, in
            place.
          </p>
        </div>
        <div className="row gap10">
          <Button icon="download" variant="default" onClick={() => setImporting(true)}>
            Import statement
          </Button>
          <Button
            icon={reconciling ? 'x' : 'check'}
            variant={reconciling ? 'default' : 'primary'}
            onClick={reconciling ? exitReconcile : enterReconcile}
          >
            {reconciling ? 'Exit reconcile' : 'Reconcile account'}
          </Button>
        </div>
      </div>

      {/* Account tabs */}
      <div className="pf-acct-tabs">
        {balances.data.map((bank) => (
          <button
            key={bank.bankAccountId}
            className={`pf-acct-tab${acctId === bank.bankAccountId ? ' active' : ''}`}
            aria-pressed={acctId === bank.bankAccountId}
            onClick={() => {
              setAcctId(bank.bankAccountId);
              exitReconcile();
            }}
          >
            <div className="pf-bankic">
              <Icon name="bank" size={16} />
            </div>
            <div className="col" style={{ alignItems: 'flex-start' }}>
              <span className="fw6 fs13">{bank.name}</span>
              <span className="t3 fs12">
                {num(bank.uncleared) !== 0 ? 'Uncleared items' : 'Reconciled'}
              </span>
            </div>
            <Money value={num(bank.book)} />
          </button>
        ))}
      </div>

      {/* Balance strip / reconcile bar */}
      {!reconciling ? (
        <Card className="pf-bank-bal">
          <div className="pf-balcell">
            <span className="pf-eyebrow">Book balance</span>
            <Money value={book} big />
          </div>
          <div className="pf-balcell">
            <span className="pf-eyebrow">Cleared balance</span>
            <span className="pf-money" style={{ fontSize: '1.5em', fontWeight: 700 }}>
              <Money value={clearedBalance} plain />
            </span>
          </div>
          <div className="pf-balcell">
            <span className="pf-eyebrow">Uncleared</span>
            <span
              className="pf-money"
              style={{
                fontSize: '1.5em',
                fontWeight: 700,
                color: num(totals?.uncleared) !== 0 ? 'var(--warn)' : 'var(--text-3)',
              }}
            >
              {num(totals?.uncleared) !== 0 ? <Money value={num(totals?.uncleared)} plain /> : '—'}
            </span>
            <span className="t3 fs12">
              {num(totals?.unclearedCount)} item{num(totals?.unclearedCount) === 1 ? '' : 's'}
            </span>
          </div>
          <div className="pf-balcell">
            <span className="pf-eyebrow">This view</span>
            <div className="row gap12">
              <span className="fs13">
                <span className="t3">In </span>
                <span style={{ color: 'var(--pos)' }}>{formatMoneyK(inView)}</span>
              </span>
              <span className="fs13">
                <span className="t3">Out </span>
                <span style={{ color: 'var(--neg)' }}>{formatMoneyK(outView)}</span>
              </span>
            </div>
          </div>
        </Card>
      ) : (
        <>
          <ReconcileBar
            statementBalance={statementBalance}
            onStatementBalanceChange={setStatementBalance}
            clearedBalance={clearedBalance}
            selectedSum={selectedSum}
            reconciled={reconciled}
            onSelectAll={toggleAll}
            onFinalize={() => finalize.mutate()}
            finalizing={finalize.isPending}
          />
          {reconcileError && (
            <p className="pf-composer-error" role="alert" style={{ marginBottom: 'var(--gap)' }}>
              {reconcileError}
            </p>
          )}
        </>
      )}

      {/* Filters */}
      {!reconciling && (
        <div className="pf-bank-filters">
          <div className="pf-search" style={{ maxWidth: 280 }}>
            <Icon name="search" size={16} />
            <input
              placeholder="Search this register…"
              aria-label="Search register"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
          </div>
          {propertyOptions.length > 0 && (
            <Select
              aria-label="Filter by property"
              value={propFilter}
              onChange={(e) => setPropFilter(e.target.value)}
            >
              <option value="all">All properties</option>
              {propertyOptions.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.label}
                </option>
              ))}
            </Select>
          )}
          <Select
            aria-label="Filter by type"
            value={typeFilter}
            onChange={(e) => setTypeFilter(e.target.value)}
          >
            <option value="all">All types</option>
            <option value="deposits">Deposits</option>
            <option value="withdrawals">Withdrawals</option>
          </Select>
          <span className="ml-auto t3 fs12">
            {filtered.length} of {rows.length} transactions
          </span>
        </div>
      )}

      {/* Register */}
      <Card className="pf-ledger-card">
        {register.isPending ? (
          <div className="pf-pad col gap8">
            {[0, 1, 2, 3].map((row) => (
              <div key={row} className="pf-skeleton" style={{ height: 20 }} />
            ))}
          </div>
        ) : register.isError ? (
          <div className="pf-pad">
            <EmptyState
              icon="alert"
              title="Couldn't load the register"
              description="Please retry in a moment."
            />
          </div>
        ) : rows.length === 0 ? (
          <div className="pf-pad">
            <EmptyState
              icon="doc"
              title="No transactions yet"
              description="Deposits and withdrawals on this account will appear here."
            />
          </div>
        ) : display.length === 0 ? (
          <div className="pf-pad">
            <EmptyState
              icon="search"
              title="No transactions match the filter"
              description="Adjust the filters above."
            />
          </div>
        ) : (
          <RegisterTable
            rows={display}
            propertyLabel={propertyLabel}
            reconciling={reconciling}
            selected={selected}
            onToggle={toggle}
            onToggleAll={toggleAll}
          />
        )}
      </Card>

      <div style={{ marginTop: 'var(--gap)' }}>
        <ReconciliationHistory bankAccountId={acctId} />
      </div>

      {importing && (
        <ImportWizard
          bankAccountId={acctId}
          onClose={() => setImporting(false)}
          onConfirmed={() => {
            setImporting(false);
            refreshAccount();
          }}
        />
      )}
    </div>
  );
}

function BankingSkeleton() {
  return (
    <div className="pf-fade">
      <div className="pf-pagehd">
        <div>
          <h2>Banking</h2>
        </div>
      </div>
      <div className="pf-acct-tabs">
        {[0, 1, 2].map((i) => (
          <Card key={i} pad>
            <div className="pf-skeleton" style={{ height: 40 }} />
          </Card>
        ))}
      </div>
      <Card pad>
        <div className="pf-skeleton" style={{ height: 60 }} />
      </Card>
    </div>
  );
}
