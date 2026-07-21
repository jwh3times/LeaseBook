import { useState } from 'react';
import { Button, Card, CardHeader, Icon, Money } from '@/design';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { asApiError } from '@/lib/apiError';
import {
  type OnboardingError,
  useSignoff,
  useVerify,
  type VarianceLine,
  type VerificationReport,
} from './onboarding';

// ─── Helpers ──────────────────────────────────────────────────────────────────

function num(v: number | string | null | undefined): number {
  if (v == null) return 0;
  return typeof v === 'number' ? v : parseFloat(v) || 0;
}

// ─── Variance line row ────────────────────────────────────────────────────────

function VarianceRow({ line }: { line: VarianceLine }) {
  const tied = num(line.variance) === 0;
  return (
    <tr>
      <td>{line.label}</td>
      <td className="num">
        <Money value={num(line.expected)} />
      </td>
      <td className="num">
        <Money value={num(line.actual)} />
      </td>
      <td className="num">
        <span className={tied ? 'ob-tied' : 'ob-untied'} aria-label={tied ? 'tied' : 'not tied'}>
          <Icon name={tied ? 'check' : 'alert'} size={14} />
          <Money value={num(line.variance)} colorize />
        </span>
      </td>
    </tr>
  );
}

// ─── Verification form ────────────────────────────────────────────────────────

interface VerificationStepProps {
  verificationId?: string;
  initialReport?: VerificationReport;
}

export function VerificationStep({
  verificationId: initialId,
  initialReport,
}: VerificationStepProps) {
  const today = new Date().toISOString().slice(0, 10);
  const [cutoverDate, setCutoverDate] = useState(today);
  const [ownerEquity, setOwnerEquity] = useState('');
  const [depositLiability, setDepositLiability] = useState('');
  const [heldPmFees, setHeldPmFees] = useState('');
  const [bankRows, setBankRows] = useState([
    { bankAccountId: '', accountCode: '', expectedBook: '' },
  ]);
  const [report, setReport] = useState<VerificationReport | null>(initialReport ?? null);
  const [reportId, setReportId] = useState<string | null>(initialId ?? null);
  const [signoffError, setSignoffError] = useState<OnboardingError | null>(null);
  const [signedOff, setSignedOff] = useState(false);

  const verify = useVerify();
  const signoff = useSignoff();

  function addBankRow() {
    setBankRows((rows) => [...rows, { bankAccountId: '', accountCode: '', expectedBook: '' }]);
  }

  function updateBankRow(i: number, field: string, value: string) {
    setBankRows((rows) => rows.map((r, idx) => (idx === i ? { ...r, [field]: value } : r)));
  }

  async function handleVerify() {
    setSignoffError(null);
    const result = await verify.mutateAsync({
      cutoverDate,
      ownerEquityTotal: parseFloat(ownerEquity) || 0,
      depositLiabilityTotal: parseFloat(depositLiability) || 0,
      // D5: a blank field means UNATTESTED (null), NOT zero. Never coerce blank → 0 here, or the
      // sign-off gate would read a fabricated attestation. A filled field sends the parsed number.
      heldPmFeesTotal: heldPmFees.trim() === '' ? null : parseFloat(heldPmFees) || 0,
      bankBookBalances: bankRows
        .filter((r) => r.bankAccountId || r.accountCode)
        .map((r) => ({
          bankAccountId: r.bankAccountId || '',
          accountCode: r.accountCode || null,
          expectedBook: parseFloat(r.expectedBook) || 0,
        })),
    });
    setReport(result);
    setReportId(result.verificationId);
  }

  async function handleSignoff() {
    if (!reportId) return;
    setSignoffError(null);
    try {
      await signoff.mutateAsync({ id: reportId });
      setSignedOff(true);
    } catch (err) {
      const e = asApiError(err, 'Sign-off failed.');
      if (e.code === 'not_tied') {
        setSignoffError({
          ...e,
          message: `Import doesn't tie — variance is ${
            report ? num(report.varianceTotal).toFixed(2) : 'unknown'
          }. Correct and re-import before signing off.`,
        });
      } else {
        setSignoffError(e);
      }
    }
  }

  const isTied = report?.isTied === true;

  if (signedOff) {
    return (
      <Card pad>
        <div className="ob-success-banner ob-success-banner--lg" role="status">
          <Icon name="check" size={20} />
          <span>Migration verified and signed off. Your org is live.</span>
        </div>
      </Card>
    );
  }

  return (
    <Card pad>
      <CardHeader
        title="Verify & sign off"
        sub="Enter your AppFolio closing figures. All lines must tie before sign-off is enabled."
      />

      <div className="ob-field">
        <label className="fs13 fw6" htmlFor="ver-cutover">
          Cutover date
        </label>
        <input
          id="ver-cutover"
          type="date"
          className="ob-date-input"
          value={cutoverDate}
          onChange={(e) => setCutoverDate(e.target.value)}
          aria-label="Cutover date"
        />
      </div>

      <div className="ob-field">
        <label className="fs13 fw6" htmlFor="ver-owner-equity">
          Owner equity total (AppFolio)
        </label>
        <input
          id="ver-owner-equity"
          type="number"
          step="0.01"
          className="ob-number-input"
          value={ownerEquity}
          onChange={(e) => setOwnerEquity(e.target.value)}
          aria-label="Owner equity total from AppFolio"
          placeholder="0.00"
        />
      </div>

      <div className="ob-field">
        <label className="fs13 fw6" htmlFor="ver-deposit">
          Deposit liability total (AppFolio)
        </label>
        <input
          id="ver-deposit"
          type="number"
          step="0.01"
          className="ob-number-input"
          value={depositLiability}
          onChange={(e) => setDepositLiability(e.target.value)}
          aria-label="Deposit liability total from AppFolio"
          placeholder="0.00"
        />
      </div>

      <div className="ob-field">
        <label className="fs13 fw6" htmlFor="ver-held-fees">
          Held PM fees total (AppFolio) — leave blank if none
        </label>
        <input
          id="ver-held-fees"
          type="number"
          step="0.01"
          className="ob-number-input"
          value={heldPmFees}
          onChange={(e) => setHeldPmFees(e.target.value)}
          aria-label="Held PM fees total from AppFolio"
          placeholder="Leave blank if none"
        />
      </div>

      <div className="ob-bank-rows">
        <p className="fs13 fw6">Bank book balances (AppFolio)</p>
        {bankRows.map((row, i) => (
          <div key={i} className="ob-bank-row">
            <input
              type="text"
              className="ob-text-input"
              placeholder="Bank account ID (optional)"
              value={row.bankAccountId}
              onChange={(e) => updateBankRow(i, 'bankAccountId', e.target.value)}
              aria-label={`Bank account ID ${i + 1}`}
            />
            <input
              type="text"
              className="ob-text-input"
              placeholder="Account code (optional)"
              value={row.accountCode}
              onChange={(e) => updateBankRow(i, 'accountCode', e.target.value)}
              aria-label={`Account code ${i + 1}`}
            />
            <input
              type="number"
              step="0.01"
              className="ob-number-input"
              placeholder="Book balance"
              value={row.expectedBook}
              onChange={(e) => updateBankRow(i, 'expectedBook', e.target.value)}
              aria-label={`Expected book balance ${i + 1}`}
            />
          </div>
        ))}
        <button type="button" className="ob-add-bank-row" onClick={addBankRow}>
          <Icon name="arrowUpRight" size={14} /> Add bank account
        </button>
      </div>

      <div className="ob-verify-actions">
        <Button
          variant="default"
          onClick={() => {
            void handleVerify();
          }}
          disabled={verify.isPending}
        >
          {verify.isPending ? 'Running…' : 'Run verification'}
        </Button>
      </div>

      {verify.isError && <ApiErrorNotice error={verify.error} fallback="Verification failed." />}

      {report && (
        <div className="ob-verification-report">
          <div className="ob-report-header">
            <p className="fs13 fw6">Verification report — {report.cutoverDate}</p>
            <span
              className={isTied ? 'ob-tied ob-tied--badge' : 'ob-untied ob-untied--badge'}
              aria-label={isTied ? 'Import is tied' : 'Import is not tied'}
            >
              <Icon name={isTied ? 'check' : 'alert'} size={14} />
              {isTied ? 'Tied' : 'Not tied'}
            </span>
          </div>

          <table className="pf-table ob-variance-table" aria-label="Verification variance report">
            <thead>
              <tr>
                <th>Line</th>
                <th className="num">Expected</th>
                <th className="num">Actual</th>
                <th className="num">Variance</th>
              </tr>
            </thead>
            <tbody>
              {report.lines.map((line) => (
                <VarianceRow key={line.key} line={line} />
              ))}
            </tbody>
          </table>

          <div className="ob-clearing-residual">
            <span className="fs13 fw6">Clearing residual (cash basis)</span>
            <Money value={num(report.clearingCash)} colorize />
            <span className="fs13 fw6">Clearing residual (accrual basis)</span>
            <Money value={num(report.clearingAccrual)} colorize />
            <span className="fs13 fw6">Total variance</span>
            <Money value={num(report.varianceTotal)} colorize />
          </div>

          {!isTied && (
            <div className="ob-error-banner" role="alert">
              <Icon name="alert" size={16} />
              <span>
                Import doesn&apos;t tie — variance is <Money value={num(report.varianceTotal)} />.
                Correct and re-import before signing off.
              </span>
            </div>
          )}

          <ApiErrorNotice error={signoffError} />

          <div className="ob-verify-actions">
            <Button
              variant="primary"
              onClick={() => {
                void handleSignoff();
              }}
              disabled={!isTied || signoff.isPending}
              aria-disabled={!isTied}
            >
              {signoff.isPending ? 'Signing off…' : 'Sign off migration'}
            </Button>
            {!isTied && (
              <span className="fs12 muted">Sign-off is disabled until the import ties.</span>
            )}
          </div>
        </div>
      )}
    </Card>
  );
}
