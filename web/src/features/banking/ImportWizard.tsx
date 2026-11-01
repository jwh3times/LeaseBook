import { useMutation } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { Badge, type BadgeTone, Button, EmptyState, Icon, Money, Select } from '@/design';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { ErrorAction } from '@/components/ErrorAction';
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
  readStatementHeaders,
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

const MAP_FIELDS = ['date', 'description', 'amount', 'debit', 'credit'] as const;
type MapField = (typeof MAP_FIELDS)[number];

/** A saved mapping's column that the uploaded file's header row does not have. */
interface MissingColumn {
  field: MapField;
  name: string;
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

  const [dateCol, setDateCol] = useState(NONE);
  const [descCol, setDescCol] = useState(NONE);
  const [amountCol, setAmountCol] = useState(NONE);
  const [debitCol, setDebitCol] = useState(NONE);
  const [creditCol, setCreditCol] = useState(NONE);
  const [useDebitCredit, setUseDebitCredit] = useState(false);
  const [missingFromFile, setMissingFromFile] = useState<MissingColumn[]>([]);

  const [importId, setImportId] = useState('');
  const [importResult, setImportResult] = useState<ImportResult | null>(null);
  const [preview, setPreview] = useState<MatchPreviewResponse | null>(null);
  const [error, setError] = useState<BankingError | null>(null);

  const savedMappings = useColumnMappings(bankAccountId);

  // The server is the one reader of the header row, so any column offered maps (#651). A mutation, not a
  // query: the file's text would make a poor cache key. Its variables are that text too, so the cache
  // keeps nothing once the wizard closes.
  const headersRead = useMutation<string[], BankingError, string>({
    mutationFn: readStatementHeaders,
    gcTime: 0,
  });
  const headers = headersRead.data ?? [];

  const onFile = async (file: File | undefined) => {
    if (!file) return;
    const text = await file.text();
    setFilename(file.name);
    setCsv(text);
    setMissingFromFile([]);
    setError(null);
    setStep('map');
    headersRead.mutate(text);
  };

  // A saved mapping can name a column this file lacks (#645). Such a column is left unmapped and named
  // in a notice, so the operator picks it from this file's header instead of submitting a map the
  // server refuses.
  const applySaved = (mapping: ColumnMap) => {
    const inFile = (name: string | null | undefined) =>
      name && headers.includes(name) ? name : NONE;
    setDateCol(inFile(mapping.date));
    setDescCol(inFile(mapping.description));
    setAmountCol(inFile(mapping.amount));
    setDebitCol(inFile(mapping.debit));
    setCreditCol(inFile(mapping.credit));
    setUseDebitCredit(!mapping.amount && (!!mapping.debit || !!mapping.credit));
    setMissingFromFile(
      MAP_FIELDS.flatMap((field) => {
        const name = mapping[field];
        return name && !headers.includes(name) ? [{ field, name }] : [];
      }),
    );
  };

  // A missing column stops blocking once the operator maps that field from this file. Only the fields
  // the chosen layout sends count: one side of a debit/credit pair would otherwise satisfy `canMap`,
  // and every row carrying only the other side would be a row error.
  const chosen: Record<MapField, string> = {
    date: dateCol,
    description: descCol,
    amount: amountCol,
    debit: debitCol,
    credit: creditCol,
  };
  const unresolved = missingFromFile.filter(
    ({ field }) =>
      chosen[field] === NONE &&
      (field === 'amount'
        ? !useDebitCredit
        : field === 'debit' || field === 'credit'
          ? useDebitCredit
          : true),
  );

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
    unresolved.length === 0 &&
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
              Columns are mapped next. Nothing is stored until you preview the matches.
            </span>
          </label>
        )}

        {step === 'map' &&
          (headersRead.isError ? (
            <div className="col gap6">
              <ApiErrorNotice error={headersRead.error} kind="read" />
              {/* A 400 is about the file itself, such as its size, and sending it again changes nothing. */}
              <ErrorAction
                error={headersRead.error}
                onRetry={
                  headersRead.error.status === 400 ? undefined : () => headersRead.mutate(csv)
                }
              />
            </div>
          ) : !headersRead.isSuccess ? (
            <div role="status" aria-label="Reading the file">
              <div className="pf-skeleton" style={{ height: 20 }} />
            </div>
          ) : headers.length === 0 ? (
            <EmptyState
              icon="doc"
              title="No columns in this file"
              description={`${filename} has no column names, so there is nothing to map. Check the file, then cancel and import it again.`}
            />
          ) : (
            <>
              {savedMappings.isPending ? (
                <div role="status" aria-label="Loading saved mappings">
                  <div className="pf-skeleton" style={{ height: 20 }} />
                </div>
              ) : savedMappings.isError ? (
                <div className="col gap6">
                  <ApiErrorNotice error={savedMappings.error} kind="read" />
                  <p className="t3 fs12">You can still map the columns manually below.</p>
                </div>
              ) : (savedMappings.data?.length ?? 0) === 0 ? (
                <EmptyState
                  icon="doc"
                  title="No saved mappings yet"
                  description="Map the columns below."
                />
              ) : (
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
              {unresolved.length > 0 && (
                <p role="status" aria-label="Saved mapping does not fit this file" className="fs12">
                  <Icon name="alert" size={14} /> This file has no column named{' '}
                  {unresolved.map(({ name }) => `“${name}”`).join(', ')}. Choose a column from this
                  file for {unresolved.length === 1 ? 'it' : 'each'} below.
                </p>
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
          ))}

        {step === 'preview' && preview && (
          <>
            {importResult && (
              <p className="t3 fs12">
                Imported {num(importResult.imported)} line
                {num(importResult.imported) === 1 ? '' : 's'}
                {num(importResult.broughtBack) > 0 &&
                  ` · ${num(importResult.broughtBack)} line${num(importResult.broughtBack) === 1 ? '' : 's'} from an earlier import not yet confirmed`}
                {num(importResult.skippedDuplicates) > 0 &&
                  ` · ${num(importResult.skippedDuplicates)} duplicate skipped`}
              </p>
            )}
            {importResult && importResult.errors.length > 0 && (
              <RowErrors errors={importResult.errors} />
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
    // A payout is confirmed as a whole: the server clears every one of its bank lines or none.
    ...(row.groupRef ? { groupRef: row.groupRef } : {}),
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

/**
 * The rows the server could not read (#667); nothing stores them, so this is the operator's one view of
 * them. The corrected file brings back any line not yet confirmed (#671), so correcting it can come first.
 */
function RowErrors({ errors }: { errors: ImportResult['errors'] }) {
  return (
    <div className="col gap6">
      <p role="alert" className="row gap8 fs13">
        <Icon name="alert" size={14} />
        <span>
          {errors.length === 1 ? '1 row was' : `${errors.length} rows were`} not imported. Correct
          the file and import it again.
        </span>
      </p>
      <div className="pf-row-errors" role="region" aria-label="Rows not imported" tabIndex={0}>
        <table className="pf-table">
          <thead>
            <tr>
              <th className="num">Line</th>
              <th>Reason</th>
            </tr>
          </thead>
          <tbody>
            {errors.map((rowError) => (
              <tr key={rowError.lineNumber}>
                <td className="num">{rowError.lineNumber}</td>
                <td>{rowError.message}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

/** One statement line answering for every bank line a processor payout posted (ADR-053). */
function PayoutMatch({ row }: { row: MatchPreviewRow }) {
  const lines = row.groupLines ?? [];
  return (
    <details>
      <summary>
        Payout {row.groupRef} — {lines.length} bank line{lines.length === 1 ? '' : 's'}, cleared
        together
      </summary>
      <ul className="col gap4 fs12" aria-label={`Bank lines of payout ${row.groupRef}`}>
        {lines.map((line) => (
          <li key={line.journalLineId} className="row gap8">
            <span>{line.description || 'Bank line'}</span>
            <Money value={num(line.amount)} colorize />
          </li>
        ))}
      </ul>
    </details>
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
                {kind === 'unmatched' ? (
                  'No register match — create a transaction'
                ) : row.groupRef ? (
                  <PayoutMatch row={row} />
                ) : null}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
