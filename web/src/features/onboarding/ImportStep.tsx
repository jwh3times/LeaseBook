import { useRef, useState } from 'react';
import { Card, CardHeader, EmptyState, Icon } from '@/design';
import {
  useImportBalances,
  useImportEntities,
  type BalanceKind,
  type EntityKind,
  type ImportBatchError,
} from './onboarding';

// ─── Entity import ────────────────────────────────────────────────────────────

interface EntityImportStepProps {
  title: string;
  description: string;
  kinds: { kind: EntityKind; label: string }[];
}

export function EntityImportStep({ title, description, kinds }: EntityImportStepProps) {
  const [selectedKind, setSelectedKind] = useState<EntityKind>(kinds[0]!.kind);
  const [filename, setFilename] = useState<string | null>(null);
  const [errors, setErrors] = useState<ImportBatchError[]>([]);
  const [result, setResult] = useState<{ rowCount: number; errorCount: number } | null>(null);
  const fileRef = useRef<HTMLInputElement>(null);
  const mutation = useImportEntities(selectedKind);

  async function handleFile(file: File) {
    setFilename(file.name);
    setErrors([]);
    setResult(null);
    const csvContent = await file.text();
    const res = await mutation.mutateAsync({
      csvContent,
      filename: file.name,
      mappingProfile: null,
    });
    setResult({ rowCount: Number(res.rowCount), errorCount: Number(res.errorCount) });
    setErrors(res.errors ?? []);
  }

  async function handleChange(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    if (file) await handleFile(file);
  }

  async function handleDrop(e: React.DragEvent) {
    e.preventDefault();
    const file = e.dataTransfer.files[0];
    if (file) await handleFile(file);
  }

  const hasErrors = errors.length > 0;
  const isSuccess = result !== null && !hasErrors;

  return (
    <Card pad>
      <CardHeader title={title} sub={description} />

      {kinds.length > 1 && (
        <fieldset className="ob-kind-fieldset">
          <legend className="fs13 fw6 muted">Import type</legend>
          <div className="row gap8 mt8">
            {kinds.map(({ kind, label }) => (
              <label key={kind} className="ob-kind-radio">
                <input
                  type="radio"
                  name="entity-kind"
                  value={kind}
                  checked={selectedKind === kind}
                  onChange={() => setSelectedKind(kind)}
                />
                {label}
              </label>
            ))}
          </div>
        </fieldset>
      )}

      <div
        className={['ob-dropzone', mutation.isPending ? 'ob-dropzone--loading' : '']
          .filter(Boolean)
          .join(' ')}
        onDragOver={(e) => e.preventDefault()}
        onDrop={(e) => {
          void handleDrop(e);
        }}
        onClick={() => fileRef.current?.click()}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            fileRef.current?.click();
          }
        }}
        role="button"
        tabIndex={0}
        aria-label="Upload CSV file"
      >
        <input
          ref={fileRef}
          type="file"
          accept=".csv,text/csv"
          aria-label="CSV file"
          className="ob-file-input"
          onChange={(e) => {
            void handleChange(e);
          }}
        />
        {mutation.isPending ? (
          <EmptyState icon="arrowUpRight" title="Importing…" description="Processing your CSV." />
        ) : filename ? (
          <div className="col gap4 align-center">
            <Icon name="check" size={20} />
            <span className="fw6">{filename}</span>
            <span className="fs12 muted">Click or drop to replace</span>
          </div>
        ) : (
          <EmptyState
            icon="arrowUpRight"
            title="Drop a CSV here or click to browse"
            description="Accepted: .csv"
          />
        )}
      </div>

      {mutation.isError && (
        <div className="ob-error-banner" role="alert">
          <Icon name="alert" size={16} />
          <span>{(mutation.error as { message?: string })?.message ?? 'Import failed.'}</span>
        </div>
      )}

      {isSuccess && (
        <div className="ob-success-banner" role="status">
          <Icon name="check" size={16} />
          <span>
            Imported {result.rowCount} row{result.rowCount !== 1 ? 's' : ''} successfully.
          </span>
        </div>
      )}

      {hasErrors && (
        <div className="ob-import-errors">
          <p className="ob-errors-title" role="alert">
            <Icon name="alert" size={14} />
            <span>
              {errors.length} error{errors.length !== 1 ? 's' : ''} — fix and re-upload:
            </span>
          </p>
          <table className="pf-table ob-error-table" aria-label="Import errors">
            <thead>
              <tr>
                <th>Row</th>
                <th>Field</th>
                <th>Reason</th>
              </tr>
            </thead>
            <tbody>
              {errors.map((err, i) => (
                <tr key={i}>
                  <td>{err.rowNumber}</td>
                  <td>{err.field}</td>
                  <td>{err.reason}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  );
}

// ─── Balance import ───────────────────────────────────────────────────────────

interface BalanceImportStepProps {
  title: string;
  description: string;
  kinds: { kind: BalanceKind; label: string }[];
  defaultCutoverDate?: string;
}

export function BalanceImportStep({
  title,
  description,
  kinds,
  defaultCutoverDate,
}: BalanceImportStepProps) {
  const today = new Date().toISOString().slice(0, 10);
  const [selectedKind, setSelectedKind] = useState<BalanceKind>(kinds[0]!.kind);
  const [cutoverDate, setCutoverDate] = useState(defaultCutoverDate ?? today);
  const [filename, setFilename] = useState<string | null>(null);
  const [errors, setErrors] = useState<ImportBatchError[]>([]);
  const [result, setResult] = useState<{ rowCount: number; errorCount: number } | null>(null);
  const fileRef = useRef<HTMLInputElement>(null);
  const mutation = useImportBalances(selectedKind);

  async function handleFile(file: File) {
    setFilename(file.name);
    setErrors([]);
    setResult(null);
    const csvContent = await file.text();
    const res = await mutation.mutateAsync({
      csvContent,
      filename: file.name,
      cutoverDate,
      mappingProfile: null,
    });
    setResult({ rowCount: Number(res.rowCount), errorCount: Number(res.errorCount) });
    setErrors(res.errors ?? []);
  }

  async function handleChange(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    if (file) await handleFile(file);
  }

  async function handleDrop(e: React.DragEvent) {
    e.preventDefault();
    const file = e.dataTransfer.files[0];
    if (file) await handleFile(file);
  }

  const hasErrors = errors.length > 0;
  const isSuccess = result !== null && !hasErrors;

  return (
    <Card pad>
      <CardHeader title={title} sub={description} />

      {kinds.length > 1 && (
        <fieldset className="ob-kind-fieldset">
          <legend className="fs13 fw6 muted">Balance type</legend>
          <div className="row gap8 mt8">
            {kinds.map(({ kind, label }) => (
              <label key={kind} className="ob-kind-radio">
                <input
                  type="radio"
                  name="balance-kind"
                  value={kind}
                  checked={selectedKind === kind}
                  onChange={() => setSelectedKind(kind)}
                />
                {label}
              </label>
            ))}
          </div>
        </fieldset>
      )}

      <div className="ob-field">
        <label className="fs13 fw6" htmlFor="cutover-date">
          Cutover date
        </label>
        <input
          id="cutover-date"
          type="date"
          className="ob-date-input"
          value={cutoverDate}
          onChange={(e) => setCutoverDate(e.target.value)}
          aria-label="Cutover date"
        />
      </div>

      <div
        className={['ob-dropzone', mutation.isPending ? 'ob-dropzone--loading' : '']
          .filter(Boolean)
          .join(' ')}
        onDragOver={(e) => e.preventDefault()}
        onDrop={(e) => {
          void handleDrop(e);
        }}
        onClick={() => fileRef.current?.click()}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            fileRef.current?.click();
          }
        }}
        role="button"
        tabIndex={0}
        aria-label="Upload CSV file"
      >
        <input
          ref={fileRef}
          type="file"
          accept=".csv,text/csv"
          aria-label="CSV file"
          className="ob-file-input"
          onChange={(e) => {
            void handleChange(e);
          }}
        />
        {mutation.isPending ? (
          <EmptyState icon="arrowUpRight" title="Importing…" description="Processing your CSV." />
        ) : filename ? (
          <div className="col gap4 align-center">
            <Icon name="check" size={20} />
            <span className="fw6">{filename}</span>
            <span className="fs12 muted">Click or drop to replace</span>
          </div>
        ) : (
          <EmptyState
            icon="arrowUpRight"
            title="Drop a CSV here or click to browse"
            description="Accepted: .csv"
          />
        )}
      </div>

      {mutation.isError && (
        <div className="ob-error-banner" role="alert">
          <Icon name="alert" size={16} />
          <span>{(mutation.error as { message?: string })?.message ?? 'Import failed.'}</span>
        </div>
      )}

      {isSuccess && (
        <div className="ob-success-banner" role="status">
          <Icon name="check" size={16} />
          <span>
            Imported {result.rowCount} row{result.rowCount !== 1 ? 's' : ''} successfully.
          </span>
        </div>
      )}

      {hasErrors && (
        <div className="ob-import-errors">
          <p className="ob-errors-title" role="alert">
            <Icon name="alert" size={14} />
            <span>
              {errors.length} error{errors.length !== 1 ? 's' : ''} — fix and re-upload:
            </span>
          </p>
          <table className="pf-table ob-error-table" aria-label="Import errors">
            <thead>
              <tr>
                <th>Row</th>
                <th>Field</th>
                <th>Reason</th>
              </tr>
            </thead>
            <tbody>
              {errors.map((err, i) => (
                <tr key={i}>
                  <td>{err.rowNumber}</td>
                  <td>{err.field}</td>
                  <td>{err.reason}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  );
}

// ─── Convenience re-export ────────────────────────────────────────────────────

export type { EntityImportStepProps, BalanceImportStepProps };
