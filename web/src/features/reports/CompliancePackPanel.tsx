import { useState } from 'react';
import { Badge, Button, Card, CardHeader, EmptyState, Icon, Input } from '@/design';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { asApiError, type ApiError } from '@/api';
import { useBankBalances } from '@/features/banking/banking';
import { SelectChip, type SelectChipOption } from './chips';
import { downloadCompliancePack, type ReportDescriptor, type ReportsError } from './reports';

// Sensible defaults: the current year to date. Every month in the range must be reconciliation-locked,
// so the operator adjusts these to a closed period before downloading.
function currentYearStart(): string {
  return `${new Date().getFullYear()}-01-01`;
}
function today(): string {
  return new Date().toISOString().slice(0, 10);
}

/**
 * Operator-facing copy for the pack's three known failures. This keys off status as well as `code`
 * because the server states the domain code in `title` on some of these and not at all on a 404 —
 * an empty 404 body still means "that trust account is gone". The branch order is the one the
 * transport-layer mapper used before ADR-025's consolidation reached this surface: status wins for
 * 422, code wins for the rest.
 */
function compliancePackMessage(e: ApiError): string | undefined {
  if (e.status === 422 || e.code === 'period_not_closed') {
    return "This period isn't closed yet — finalize the reconciliation for every month in the range first.";
  }
  if (e.status === 404) return 'That trust account could not be found.';
  if (e.status === 400 || e.code === 'invalid_period') {
    return 'The start date must be on or before the end date.';
  }
  return undefined;
}

interface CompliancePackPanelProps {
  report: ReportDescriptor;
  /** PMAdmin gate — the pack carries the audit-log extract, so only admins may generate it. */
  isAdmin: boolean;
}

/**
 * The WP-8 Trust Compliance Pack builder. Unlike the generic report builder, its primary action is a
 * ZIP download (not a live preview / CSV): the operator picks one trust account and a from/to range,
 * then downloads an audit-ready pack. Every month in the range must be reconciliation-locked; an open
 * period surfaces a clear, non-color-only error (WCAG 1.4.1). PMAdmin-only.
 */
export function CompliancePackPanel({ report, isAdmin }: CompliancePackPanelProps) {
  const [bankAccountId, setBankAccountId] = useState<string | null>(null);
  const [from, setFrom] = useState(currentYearStart);
  const [to, setTo] = useState(today);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ReportsError | null>(null);
  const [done, setDone] = useState(false);

  const banksQuery = useBankBalances({ enabled: isAdmin });
  const bankOptions: SelectChipOption[] = (banksQuery.data ?? []).map((b) => ({
    id: b.bankAccountId,
    label: b.name,
  }));
  const selectedBankLabel = bankAccountId
    ? (bankOptions.find((b) => b.id === bankAccountId)?.label ?? 'Selected')
    : 'Choose…';

  const rangeValid = from !== '' && to !== '' && from <= to;
  const canDownload = isAdmin && bankAccountId != null && rangeValid && !busy;

  const handleDownload = async () => {
    if (!bankAccountId) return;
    setError(null);
    setDone(false);
    setBusy(true);
    try {
      await downloadCompliancePack(bankAccountId, from, to);
      setDone(true);
    } catch (e) {
      const err = asApiError(e, 'Download failed. Please retry.');
      setError({ ...err, message: compliancePackMessage(err) ?? err.message });
    } finally {
      setBusy(false);
    }
  };

  if (!isAdmin) {
    return (
      <Card pad>
        <EmptyState
          icon="alert"
          title="Admin only"
          description="The trust compliance pack contains the audit-log extract, so only a PM admin can generate it."
        />
      </Card>
    );
  }

  return (
    <Card className="pf-builder">
      <CardHeader
        title={
          <div className="row gap10">
            <div className="pf-report-ic" aria-hidden="true">
              <Icon name="doc" size={18} />
            </div>
            <div>
              <span>{report.name}</span>
              <div className="sub">{report.category} report</div>
            </div>
          </div>
        }
        actions={
          <Button
            icon="download"
            variant="primary"
            onClick={() => void handleDownload()}
            disabled={!canDownload}
          >
            {busy ? 'Preparing…' : 'Download pack'}
          </Button>
        }
      />

      {/* Filters strip: trust account + from/to range */}
      <div className="pf-builder-filters" role="group" aria-label="Compliance pack filters">
        <SelectChip
          label="Trust account"
          value={selectedBankLabel}
          options={bankOptions}
          loading={banksQuery.isPending}
          onSelect={(id) => {
            setBankAccountId(id);
            setDone(false);
            setError(null);
          }}
        />
        <div className="pf-pack-dates">
          <label className="pf-pack-date">
            <span className="pf-fchip-label">From</span>
            <Input
              type="date"
              aria-label="From date"
              value={from}
              max={to || undefined}
              onChange={(e) => {
                setFrom(e.target.value);
                setDone(false);
                setError(null);
              }}
            />
          </label>
          <label className="pf-pack-date">
            <span className="pf-fchip-label">To</span>
            <Input
              type="date"
              aria-label="To date"
              value={to}
              min={from || undefined}
              onChange={(e) => {
                setTo(e.target.value);
                setDone(false);
                setError(null);
              }}
            />
          </label>
        </div>
      </div>

      {!rangeValid && (
        <p className="pf-composer-error" role="alert" style={{ padding: '8px var(--card-pad)' }}>
          The start date must be on or before the end date.
        </p>
      )}

      <ApiErrorNotice error={error} style={{ padding: '8px var(--card-pad)' }} />

      {done && (
        <p className="pf-pack-ok" role="status" style={{ padding: '8px var(--card-pad)' }}>
          <Icon name="check" size={14} /> Your compliance pack is downloading.
        </p>
      )}

      {/* What's inside — audit documents composed into the pack */}
      <div className="pf-pack-body">
        <div className="pf-preview-bar">
          <span className="pf-eyebrow">Audit pack contents</span>
          <span className="t3 fs12">One trust account · a closed period</span>
        </div>
        <div className="pf-pad">
          <ul className="pf-pack-list">
            {[
              'Trust account ledger',
              'Security deposit register',
              'Reconciliation history',
              'Audit-log extract',
            ].map((item) => (
              <li key={item} className="row gap8">
                <Icon name="check" size={15} />
                <span>{item}</span>
              </li>
            ))}
          </ul>
          <div className="pf-pack-note">
            <Badge tone="accent" soft dot>
              Closed period required
            </Badge>
            <span className="t3 fs12">
              Every month in the range must be reconciliation-locked for the selected trust account
              before a pack can be generated.
            </span>
          </div>
        </div>
      </div>
    </Card>
  );
}
