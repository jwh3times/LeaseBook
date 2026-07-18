import { useState } from 'react';
import { Badge, Button, Card, CardHeader, EmptyState, Icon, Input } from '@/design';
import { useBankBalances } from '@/features/banking/banking';
import { SelectChip, type SelectChipOption } from './chips';
import { downloadCompliancePack, type ReportDescriptor } from './reports';

// Sensible defaults: the current year to date. The period-end month must be reconciliation-locked,
// so the operator adjusts these to a closed period before downloading.
function currentYearStart(): string {
  return `${new Date().getFullYear()}-01-01`;
}
function today(): string {
  return new Date().toISOString().slice(0, 10);
}

interface CompliancePackPanelProps {
  report: ReportDescriptor;
  /** PMAdmin gate — the pack carries the audit-log extract, so only admins may generate it. */
  isAdmin: boolean;
}

/**
 * The WP-8 Trust Compliance Pack builder. Unlike the generic report builder, its primary action is a
 * ZIP download (not a live preview / CSV): the operator picks one trust account and a from/to range,
 * then downloads an audit-ready pack. The period-end month must be reconciliation-locked; an open
 * period surfaces a clear, non-color-only error (WCAG 1.4.1). PMAdmin-only.
 */
export function CompliancePackPanel({ report, isAdmin }: CompliancePackPanelProps) {
  const [bankAccountId, setBankAccountId] = useState<string | null>(null);
  const [from, setFrom] = useState(currentYearStart);
  const [to, setTo] = useState(today);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
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
      const message =
        e && typeof e === 'object' && 'message' in e
          ? String((e as { message: unknown }).message)
          : 'Download failed. Please retry.';
      setError(message);
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

      {error && (
        <p
          className="pf-composer-error row gap6"
          role="alert"
          style={{ padding: '8px var(--card-pad)' }}
        >
          <Icon name="alert" size={14} /> {error}
        </p>
      )}

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
              The period-end month must be reconciliation-locked for the selected trust account
              before a pack can be generated.
            </span>
          </div>
        </div>
      </div>
    </Card>
  );
}
