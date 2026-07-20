import { useMutation } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { Badge, type BadgeTone, Button, Icon, Money, Select } from '@/design';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { Modal } from '@/components/Modal';
import {
  type BankingError,
  type ColumnMap,
  confirmMatches,
  type ConfirmDecision,
  fetchMatchPreview,
  importStatement,
  type ImportResult,
  type MatchPreviewResponse,
  type MatchPreviewRow,
  useColumnMappings,
} from './banking';

interface ImportWizardProps {
  bankAccountId: string;
  onClose: () => void;
  /** Called after a confirm clears lines → the page invalidates the register. */
  onConfirmed: () => void;
}

type Step = 'upload' | 'map' | 'preview';

const KIND_META: Record<string, { label: string; tone: BadgeTone }> = {
  matched: { label: 'Matched', tone: 'pos' },
  suggested: { label: 'Suggested', tone: 'accent' },
  unmatched: { label: 'Unmatched', tone: 'warn' },
};

const NONE = '';

function parseHeaders(csv: string): string[] {
  const firstLine = csv.split(/\r?\n/).find((line) => line.trim() !== '') ?? '';
  return firstLine.split(',').map((h) => h.trim().replace(/^"|"$/g, ''));
}

/**
 * The CSV import wizard (ADR-015): upload → map columns (with saved-mapping shortcuts) → match preview
 * (matched / suggested / unmatched groups) → confirm, which clears the matched lines through the clearance
 * port. Mirrors `screen-bank` import affordances against the live import/match endpoints.
 */
export function ImportWizard({ bankAccountId, onClose, onConfirmed }: ImportWizardProps) {
  const [step, setStep] = useState<Step>('upload');
  const [filename, setFilename] = useState('');
  const [csv, setCsv] = useState('');
  const [headers, setHeaders] = useState<string[]>([]);

  const [dateCol, setDateCol] = useState(NONE);
  const [descCol, setDescCol] = useState(NONE);
  const [amountCol, setAmountCol] = useState(NONE);
  const [debitCol, setDebitCol] = useState(NONE);
  const [creditCol, setCreditCol] = useState(NONE);
  const [useDebitCredit, setUseDebitCredit] = useState(false);

  const [importId, setImportId] = useState('');
  const [importResult, setImportResult] = useState<ImportResult | null>(null);
  const [preview, setPreview] = useState<MatchPreviewResponse | null>(null);
  const [error, setError] = useState<BankingError | null>(null);

  const savedMappings = useColumnMappings(bankAccountId);

  const onFile = async (file: File | undefined) => {
    if (!file) return;
    const text = await file.text();
    setFilename(file.name);
    setCsv(text);
    setHeaders(parseHeaders(text));
    setError(null);
    setStep('map');
  };

  const applySaved = (mapping: ColumnMap) => {
    setDateCol(mapping.date);
    setDescCol(mapping.description);
    setAmountCol(mapping.amount ?? NONE);
    setDebitCol(mapping.debit ?? NONE);
    setCreditCol(mapping.credit ?? NONE);
    setUseDebitCredit(!mapping.amount && (!!mapping.debit || !!mapping.credit));
  };

  const columnMap = useMemo<ColumnMap>(
    () => ({
      date: dateCol,
      description: descCol,
      ...(useDebitCredit
        ? { debit: debitCol || null, credit: creditCol || null }
        : { amount: amountCol }),
    }),
    [dateCol, descCol, amountCol, debitCol, creditCol, useDebitCredit],
  );

  const importMutation = useMutation<ImportResult, BankingError>({
    mutationFn: () => importStatement(bankAccountId, { filename, csvContent: csv, columnMap }),
    onSuccess: async (result) => {
      setImportResult(result);
      setImportId(result.importId);
      setPreview(await fetchMatchPreview(result.importId));
      setStep('preview');
    },
    onError: (err) => setError(err),
  });

  const confirmMutation = useMutation<unknown, BankingError>({
    mutationFn: () => confirmMatches(importId, buildDecisions(preview)),
    onSuccess: () => onConfirmed(),
    onError: (err) => setError(err),
  });

  const canMap =
    dateCol !== NONE &&
    descCol !== NONE &&
    (useDebitCredit ? debitCol !== NONE || creditCol !== NONE : amountCol !== NONE);

  return (
    <Modal
      title="Import bank statement"
      onClose={onClose}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          {step === 'map' && (
            <Button
              variant="primary"
              icon="arrowUpRight"
              disabled={!canMap || importMutation.isPending}
              onClick={() => {
                setError(null);
                importMutation.mutate();
              }}
            >
              Preview matches
            </Button>
          )}
          {step === 'preview' && (
            <Button
              variant="primary"
              icon="check"
              disabled={confirmMutation.isPending || !preview || preview.rows.length === 0}
              onClick={() => {
                setError(null);
                confirmMutation.mutate();
              }}
            >
              Confirm &amp; clear
            </Button>
          )}
        </>
      }
    >
      <div className="pf-modal-body col gap12">
        <div className="pf-wizard-steps">
          <span className={`step${step === 'upload' ? ' active' : ''}`}>1 · Upload</span>
          <Icon name="chevronRight" size={12} />
          <span className={`step${step === 'map' ? ' active' : ''}`}>2 · Map columns</span>
          <Icon name="chevronRight" size={12} />
          <span className={`step${step === 'preview' ? ' active' : ''}`}>
            3 · Review &amp; confirm
          </span>
        </div>

        {step === 'upload' && (
          <label className="col gap6">
            <span className="pf-eyebrow">Statement CSV</span>
            <input
              type="file"
              accept=".csv,text/csv"
              aria-label="Statement CSV"
              onChange={(e) => void onFile(e.target.files?.[0])}
            />
            <span className="t3 fs12">
              The file is read in your browser; columns are mapped next.
            </span>
          </label>
        )}

        {step === 'map' && (
          <>
            {(savedMappings.data?.length ?? 0) > 0 && (
              <label className="col gap6">
                <span className="pf-eyebrow">Saved mapping</span>
                <Select
                  aria-label="Saved mapping"
                  defaultValue=""
                  onChange={(e) => {
                    const found = savedMappings.data?.find((m) => m.id === e.target.value);
                    if (found) applySaved(found.columnMap);
                  }}
                >
                  <option value="">Choose a saved mapping…</option>
                  {savedMappings.data?.map((m) => (
                    <option key={m.id} value={m.id}>
                      {m.name}
                    </option>
                  ))}
                </Select>
              </label>
            )}
            <p className="t3 fs12">{filename}</p>
            <div className="pf-map-grid">
              <ColumnSelect
                label="Date column"
                value={dateCol}
                onChange={setDateCol}
                headers={headers}
              />
              <ColumnSelect
                label="Description column"
                value={descCol}
                onChange={setDescCol}
                headers={headers}
              />
              {useDebitCredit ? (
                <>
                  <ColumnSelect
                    label="Debit column"
                    value={debitCol}
                    onChange={setDebitCol}
                    headers={headers}
                  />
                  <ColumnSelect
                    label="Credit column"
                    value={creditCol}
                    onChange={setCreditCol}
                    headers={headers}
                  />
                </>
              ) : (
                <ColumnSelect
                  label="Amount column"
                  value={amountCol}
                  onChange={setAmountCol}
                  headers={headers}
                />
              )}
            </div>
            <label className="row gap8 fs13">
              <input
                type="checkbox"
                className="pf-check"
                checked={useDebitCredit}
                onChange={(e) => setUseDebitCredit(e.target.checked)}
                aria-label="Separate debit and credit columns"
              />
              This statement uses separate debit / credit columns
            </label>
          </>
        )}

        {step === 'preview' && preview && (
          <>
            {importResult && (
              <p className="t3 fs12">
                Imported {num(importResult.imported)} line
                {num(importResult.imported) === 1 ? '' : 's'}
                {num(importResult.skippedDuplicates) > 0 &&
                  ` · ${num(importResult.skippedDuplicates)} duplicate skipped`}
                {importResult.errors.length > 0 && ` · ${importResult.errors.length} row error(s)`}
              </p>
            )}
            <MatchGroup kind="matched" rows={preview.rows} />
            <MatchGroup kind="suggested" rows={preview.rows} />
            <MatchGroup kind="unmatched" rows={preview.rows} />
            {preview.rows.length === 0 && <p className="t3 fs13">No statement lines to review.</p>}
          </>
        )}

        <ApiErrorNotice error={error} />
      </div>
    </Modal>
  );
}

function num(value: number | string): number {
  return typeof value === 'number' ? value : Number(value);
}

function buildDecisions(preview: MatchPreviewResponse | null): ConfirmDecision[] {
  if (!preview) return [];
  return preview.rows.map((row) => ({
    statementLineId: row.statementLineId,
    journalLineId: row.journalLineId,
    kind: row.kind,
  }));
}

function ColumnSelect({
  label,
  value,
  onChange,
  headers,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  headers: string[];
}) {
  return (
    <label className="col gap6">
      <span className="pf-eyebrow">{label}</span>
      <Select aria-label={label} value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">Select…</option>
        {headers.map((h) => (
          <option key={h} value={h}>
            {h}
          </option>
        ))}
      </Select>
    </label>
  );
}

function MatchGroup({ kind, rows }: { kind: string; rows: MatchPreviewRow[] }) {
  const group = rows.filter((r) => r.kind === kind);
  if (group.length === 0) return null;
  const meta = KIND_META[kind] ?? { label: kind, tone: 'neutral' as BadgeTone };
  return (
    <div className="pf-match-group">
      <h4 className="row gap8">
        <Badge tone={meta.tone} soft dot>
          {meta.label}
        </Badge>
        <span className="t3 fs12">{group.length}</span>
      </h4>
      <table className="pf-table">
        <tbody>
          {group.map((row) => (
            <tr key={row.statementLineId}>
              <td className="muted" style={{ whiteSpace: 'nowrap' }}>
                {row.date}
              </td>
              <td className="strong">{row.description}</td>
              <td className="num">
                <Money value={num(row.amount)} colorize />
              </td>
              <td className="muted">
                {kind === 'unmatched' ? 'No register match — create a transaction' : null}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
