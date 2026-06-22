import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
import { Avatar, Button, Card, Icon, Money } from '@/design';
import {
  deliverStatement,
  downloadStatement,
  monthLabel,
  num,
  type FiduciaryPanel,
  type ReportsError,
  type StatementFilters,
  type StatementSectionView,
  type StatementView,
} from './reports';

// ---- StatementSection -------------------------------------------------------

interface StatementSectionProps {
  section: StatementSectionView;
}

function StatementSection({ section }: StatementSectionProps) {
  return (
    <div className="pf-stmt-section" role="region" aria-label={section.title}>
      <div className="pf-stmt-sectionhd">
        <span>{section.title}</span>
        <Money value={num(section.subtotal)} colorize />
      </div>
      {section.lines.map((line) => (
        <div key={line.entryId} className="pf-stmt-line">
          <div className="col" style={{ gap: 2 }}>
            <span>{line.description}</span>
            {line.propertyAddress && <span className="t3 fs12">{line.propertyAddress}</span>}
          </div>
          <Money value={num(line.amount)} colorize />
        </div>
      ))}
    </div>
  );
}

// ---- FiduciaryChecks --------------------------------------------------------

interface FiduciaryChecksProps {
  panel: FiduciaryPanel;
}

function FiduciaryChecks({ panel }: FiduciaryChecksProps) {
  const checks = [
    {
      pass: panel.pmIncomeExcluded,
      label: 'PM income excluded',
      detail:
        'Management fees and PM-sourced income are never included as owner income on this statement.',
    },
    {
      pass: panel.depositsRecognizedOnApplication,
      label: 'Deposits recognized on application',
      detail:
        'Applied security deposits appear as income in the period applied, not when collected.',
    },
    {
      pass: panel.balanced,
      label: `${Math.abs(num(panel.variance)) < 0.005 ? '$0.00' : '$' + Math.abs(num(panel.variance)).toFixed(2)} variance`,
      detail: panel.balanced
        ? 'Ledger and statement match — no unexplained difference.'
        : `There is a $${Math.abs(num(panel.variance)).toFixed(2)} unexplained variance. Review journal entries.`,
    },
  ];

  return (
    <div className="pf-fiduciary pf-card">
      <div className="pf-fid-hd">
        <Icon name="check" size={16} aria-hidden="true" />
        <b>Fiduciary integrity</b>
      </div>
      <div className="pf-fid-checks" role="list">
        {checks.map((c) => (
          <div key={c.label} className="pf-fid-check" role="listitem">
            <div
              className={`pf-fid-check-icon ${c.pass ? 'pass' : 'fail'}`}
              aria-label={c.pass ? 'Pass' : 'Warning'}
            >
              <Icon name={c.pass ? 'check' : 'alert'} size={10} />
            </div>
            <div>
              <b>{c.label}.</b> {c.detail}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

// ---- PeriodPicker -----------------------------------------------------------

const MONTHS_SHORT = [
  'Jan',
  'Feb',
  'Mar',
  'Apr',
  'May',
  'Jun',
  'Jul',
  'Aug',
  'Sep',
  'Oct',
  'Nov',
  'Dec',
];

const CURRENT_YEAR = new Date().getFullYear();
const YEAR_OPTIONS = [CURRENT_YEAR, CURRENT_YEAR - 1, CURRENT_YEAR - 2];

interface PeriodPickerProps {
  filters: StatementFilters;
  onChange: (next: StatementFilters) => void;
}

function PeriodPicker({ filters, onChange }: PeriodPickerProps) {
  const [open, setOpen] = useState(false);

  return (
    <div className="pf-filter-wrap">
      <Button
        icon="filter"
        variant="default"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        aria-haspopup="dialog"
      >
        {monthLabel(filters.year, filters.month)}
      </Button>
      {open && (
        <div className="pf-filter-popover" role="dialog" aria-label="Select period">
          <div className="pf-period-label">Year</div>
          <div className="row gap6">
            {YEAR_OPTIONS.map((y) => (
              <button
                key={y}
                className={`pf-basis-btn${filters.year === y ? ' active' : ''}`}
                onClick={() => onChange({ ...filters, year: y })}
                aria-pressed={filters.year === y}
              >
                {y}
              </button>
            ))}
          </div>
          <div className="pf-period-label" style={{ marginTop: 10 }}>
            Month
          </div>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 6 }}>
            {MONTHS_SHORT.map((m, i) => (
              <button
                key={m}
                className={`pf-basis-btn${filters.month === i + 1 ? ' active' : ''}`}
                onClick={() => {
                  onChange({ ...filters, month: i + 1 });
                  setOpen(false);
                }}
                aria-pressed={filters.month === i + 1}
              >
                {m}
              </button>
            ))}
          </div>
          <div className="pf-period-label" style={{ marginTop: 10 }}>
            Basis
          </div>
          <div className="row gap6">
            {(['cash', 'accrual'] as const).map((b) => (
              <button
                key={b}
                className={`pf-basis-btn${filters.basis === b ? ' active' : ''}`}
                onClick={() => onChange({ ...filters, basis: b })}
                aria-pressed={filters.basis === b}
              >
                {b === 'cash' ? 'Cash' : 'Accrual'}
              </button>
            ))}
          </div>
          <div style={{ marginTop: 10, textAlign: 'right' }}>
            <Button variant="primary" onClick={() => setOpen(false)}>
              Done
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}

// ---- OwnerStatementView -----------------------------------------------------

export interface OwnerStatementViewProps {
  ownerId: string;
  statement: StatementView;
  filters: StatementFilters;
  onFiltersChange: (next: StatementFilters) => void;
}

export function OwnerStatementView({
  ownerId,
  statement,
  filters,
  onFiltersChange,
}: OwnerStatementViewProps) {
  const [deliverStatus, setDeliverStatus] = useState<'idle' | 'pending' | 'queued' | 'error'>(
    'idle',
  );
  const [deliverError, setDeliverError] = useState<string | null>(null);
  const [downloadError, setDownloadError] = useState<string | null>(null);

  const beginning = num(statement.beginning);
  const ending = num(statement.ending);

  const initials =
    statement.ownerName
      .split(' ')
      .map((w) => w[0] ?? '')
      .slice(0, 2)
      .join('')
      .toUpperCase() || '?';

  const bank = statement.fiduciary.latestReconciledBank;

  const deliver = useMutation<void, ReportsError>({
    mutationFn: async () => {
      setDeliverError(null);
      await deliverStatement(ownerId, filters);
    },
    onSuccess: () => setDeliverStatus('queued'),
    onError: (err) => {
      setDeliverStatus('error');
      setDeliverError(err.message);
    },
  });

  const handlePdf = async () => {
    setDownloadError(null);
    try {
      await downloadStatement(ownerId, filters, 'pdf');
    } catch (e) {
      setDownloadError(e instanceof Error ? e.message : 'Download failed');
    }
  };

  const handleCsv = async () => {
    setDownloadError(null);
    try {
      await downloadStatement(ownerId, filters, 'csv');
    } catch (e) {
      setDownloadError(e instanceof Error ? e.message : 'Download failed');
    }
  };

  return (
    <div className="pf-fade">
      <div className="pf-pagehd">
        <div>
          <h2>Owner statement</h2>
          <p>
            {statement.ownerName}
            {statement.propertyAddress ? ` · ${statement.propertyAddress}` : ''} ·{' '}
            {monthLabel(num(statement.year), num(statement.month))} · {statement.basis} basis
          </p>
        </div>
        <div className="row gap10" style={{ flexWrap: 'wrap' }}>
          <PeriodPicker filters={filters} onChange={onFiltersChange} />
          <Button icon="doc" variant="default" onClick={() => void handlePdf()}>
            PDF
          </Button>
          <Button icon="download" variant="primary" onClick={() => void handleCsv()}>
            Export CSV
          </Button>
        </div>
      </div>

      {downloadError && (
        <p className="pf-composer-error" role="alert" style={{ marginBottom: 'var(--gap)' }}>
          {downloadError}
        </p>
      )}

      <div className="pf-stmt-layout">
        {/* Statement document */}
        <Card className="pf-stmt-doc">
          {/* Header strip */}
          <div className="pf-stmt-top">
            <div className="row gap12">
              <Avatar initials={initials} size={46} />
              <div className="col">
                <span className="fw7" style={{ fontSize: 17, letterSpacing: '-0.01em' }}>
                  {statement.ownerName}
                </span>
                <span className="t3 fs13">
                  {statement.propertyAddress ?? 'All properties'} · Statement period{' '}
                  {monthLabel(num(statement.year), num(statement.month))}
                </span>
              </div>
            </div>
          </div>

          {/* Beginning */}
          <div className="pf-stmt-begin">
            <span>Beginning balance</span>
            <Money value={beginning} />
          </div>

          {/* Sections */}
          {statement.sections.map((section) => (
            <StatementSection key={section.key} section={section} />
          ))}

          {/* Ending */}
          <div className="pf-stmt-end">
            <span>Ending balance</span>
            <Money value={ending} big />
          </div>
        </Card>

        {/* Sidebar */}
        <div className="col" style={{ gap: 'var(--gap)' }}>
          {/* Fiduciary integrity */}
          <FiduciaryChecks panel={statement.fiduciary} />

          {/* Reconciles-to card */}
          <Card pad>
            <div className="col gap12">
              <span className="pf-eyebrow">This statement reconciles to</span>
              {bank ? (
                <div className="pf-bankrow" aria-label="Bank account reconciled">
                  <div className="pf-bankic">
                    <Icon name="bank" size={16} />
                  </div>
                  <div className="col" style={{ flex: 1, alignItems: 'flex-start' }}>
                    <span className="fw6 fs13">Trust account</span>
                    <span className="t3 fs12">
                      Reconciled through {monthLabel(num(bank.year), num(bank.month))}
                    </span>
                  </div>
                  <Icon name="chevronRight" size={16} style={{ color: 'var(--text-3)' }} />
                </div>
              ) : (
                <p className="t3 fs13">No reconciled bank account on record for this period.</p>
              )}
              {statement.fiduciary.balanced ? (
                <div className="pf-recon-check" role="status">
                  <Icon name="check" size={15} aria-hidden="true" />
                  <span>
                    Ledger and statement match —{' '}
                    <b>${Math.abs(num(statement.fiduciary.variance)).toFixed(2)} variance</b>
                  </span>
                </div>
              ) : (
                <div className="pf-recon-warn" role="alert">
                  <Icon name="alert" size={15} aria-hidden="true" />
                  <span>
                    Unexplained variance:{' '}
                    <b>${Math.abs(num(statement.fiduciary.variance)).toFixed(2)}</b>
                  </span>
                </div>
              )}
            </div>
          </Card>

          {/* Deliver */}
          <Card pad>
            <div className="col gap10">
              <span className="pf-eyebrow">Deliver statement</span>
              <div className="pf-deliver-row">
                <Button
                  icon="doc"
                  variant="default"
                  onClick={() => {
                    setDeliverStatus('pending');
                    deliver.mutate();
                  }}
                  disabled={deliver.isPending || deliverStatus === 'queued'}
                >
                  {deliver.isPending ? 'Sending…' : 'Deliver to owner'}
                </Button>
                {deliverStatus === 'queued' && (
                  <span className="pf-deliver-status ok" role="status">
                    <Icon name="check" size={13} /> Queued for delivery
                  </span>
                )}
                {deliverStatus === 'error' && deliverError && (
                  <span className="pf-deliver-status error" role="alert">
                    <Icon name="alert" size={13} /> {deliverError}
                  </span>
                )}
              </div>
            </div>
          </Card>
        </div>
      </div>
    </div>
  );
}
