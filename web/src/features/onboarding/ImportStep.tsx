import { useRef, useState } from 'react';
import { Button, Card, CardHeader, EmptyState, Icon } from '@/design';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import {
  useImportBalances,
  useImportEntities,
  useSupersedeBalances,
  type BalanceKind,
  type EntityKind,
  type ImportBatchError,
  type ImportOutcomeCounts,
} from './onboarding';

// ─── Entity import ────────────────────────────────────────────────────────────

interface EntityImportStepProps {
  title: string;
  description: string;
  kinds: { kind: EntityKind; label: string }[];
  /**
   * Advances the wizard to the next step. Rendered as an explicit "Continue →" button so the
   * operator imports every kind on this step before moving on — the step never auto-advances.
   */
  onContinue?: () => void;
}

export function EntityImportStep({ title, description, kinds, onContinue }: EntityImportStepProps) {
  const [selectedKind, setSelectedKind] = useState<EntityKind>(kinds[0]!.kind);
  const [filename, setFilename] = useState<string | null>(null);
  const [errors, setErrors] = useState<ImportBatchError[]>([]);
  const [result, setResult] = useState<{ rowCount: number; errorCount: number } | null>(null);
  // Tracks which kinds imported cleanly, so the "Continue" affordance only appears once the
  // operator has imported at least one kind on this step.
  const [importedKinds, setImportedKinds] = useState<Set<EntityKind>>(new Set());
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
    const errorCount = Number(res.errorCount);
    setResult({ rowCount: Number(res.rowCount), errorCount });
    setErrors(res.errors ?? []);
    if (errorCount === 0) {
      setImportedKinds((prev) => new Set(prev).add(selectedKind));
    }
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
  const canContinue = importedKinds.size > 0;

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
                  onChange={() => {
                    setSelectedKind(kind);
                    // Reset the per-import banner/error state so a prior kind's "Imported N rows"
                    // banner doesn't carry over to the newly-selected kind.
                    setFilename(null);
                    setResult(null);
                    setErrors([]);
                  }}
                />
                {label}
                {importedKinds.has(kind) && (
                  <Icon name="check" size={14} aria-label={`${label} imported`} />
                )}
              </label>
            ))}
          </div>
        </fieldset>
      )}

      {/* File input BEFORE (not inside) the dropzone button: nesting <input type="file"> inside
          role="button" triggers axe nested-interactive (WCAG 4.1.2) because AT can focus file
          inputs even with tabIndex=-1/aria-hidden when inside a button context.
          display:none + programmatic .click() is the standard pattern; the picker still opens. */}
      <input
        ref={fileRef}
        type="file"
        accept=".csv,text/csv"
        aria-label="CSV file"
        tabIndex={-1}
        style={{ display: 'none' }}
        onChange={(e) => {
          void handleChange(e);
        }}
      />
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

      {mutation.isError && <ApiErrorNotice error={mutation.error} fallback="Import failed." />}

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

      {onContinue && (
        <div className="ob-step-actions row gap8 align-center mt16">
          <Button
            variant="primary"
            onClick={onContinue}
            disabled={!canContinue}
            aria-disabled={!canContinue}
          >
            Continue →
          </Button>
          {!canContinue && (
            <span className="fs12 muted">Import at least one entity type to continue.</span>
          )}
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
  /**
   * Advances the wizard to the next step. Rendered as an explicit "Continue →" button so the
   * operator imports each balance kind before moving on — the step never auto-advances.
   */
  onContinue?: () => void;
}

export function BalanceImportStep({
  title,
  description,
  kinds,
  defaultCutoverDate,
  onContinue,
}: BalanceImportStepProps) {
  const today = new Date().toISOString().slice(0, 10);
  const [selectedKind, setSelectedKind] = useState<BalanceKind>(kinds[0]!.kind);
  const [cutoverDate, setCutoverDate] = useState(defaultCutoverDate ?? today);
  const [filename, setFilename] = useState<string | null>(null);
  const [errors, setErrors] = useState<ImportBatchError[]>([]);
  const [result, setResult] = useState<{ rowCount: number; errorCount: number } | null>(null);
  // Corrected re-import (supersede): operator opts in per-upload when a previously-imported
  // balance kind needs correcting rather than a first-time import. `counts` holds the per-outcome
  // breakdown from the last import/supersede response so the success banner can differentiate.
  const [supersede, setSupersede] = useState(false);
  const [counts, setCounts] = useState<ImportOutcomeCounts | null>(null);
  // Tracks which balance kinds imported cleanly, so the "Continue" affordance only appears once
  // the operator has imported at least one kind on this step.
  const [importedKinds, setImportedKinds] = useState<Set<BalanceKind>>(new Set());
  const fileRef = useRef<HTMLInputElement>(null);
  const mutation = useImportBalances(selectedKind);
  const supersedeMutation = useSupersedeBalances(selectedKind);
  const isPending = mutation.isPending || supersedeMutation.isPending;

  async function handleFile(file: File) {
    setFilename(file.name);
    setErrors([]);
    setResult(null);
    setCounts(null);
    const csvContent = await file.text();
    const res = await (supersede ? supersedeMutation : mutation).mutateAsync({
      csvContent,
      filename: file.name,
      cutoverDate,
      mappingProfile: null,
    });
    const errorCount = Number(res.errorCount);
    setResult({ rowCount: Number(res.rowCount), errorCount });
    setErrors(res.errors ?? []);
    setCounts(res.counts ?? null);
    if (errorCount === 0) {
      setImportedKinds((prev) => new Set(prev).add(selectedKind));
    }
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
  const canContinue = importedKinds.size > 0;

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
                  onChange={() => {
                    setSelectedKind(kind);
                    // Reset the per-import banner/error state so a prior kind's "Imported N rows"
                    // banner doesn't carry over to the newly-selected kind. Supersede is also
                    // reset: it's a per-kind, per-upload intent ("I'm correcting this balance
                    // kind's already-imported figures"), and a kind switch usually means the
                    // operator is now importing a *different* kind for the first time — leaving
                    // supersede checked would silently route that first-time import to the
                    // supersede endpoint, which 409s with nothing_to_supersede.
                    setFilename(null);
                    setResult(null);
                    setErrors([]);
                    setSupersede(false);
                    setCounts(null);
                  }}
                />
                {label}
                {importedKinds.has(kind) && (
                  <Icon name="check" size={14} aria-label={`${label} imported`} />
                )}
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

      <div className="ob-field">
        <label className="fs13 row gap8 align-center">
          <input
            type="checkbox"
            checked={supersede}
            onChange={(e) => setSupersede(e.target.checked)}
            aria-label="This is a corrected re-import (supersede)"
          />
          This is a corrected re-import (supersede)
        </label>
        {supersede && (
          <p className="fs12 muted mt4">
            Only figures that changed are corrected (reversal + corrected entry). Rows left out of
            the file are untouched; submit a row with $0.00 to remove its position. Re-run
            verification afterwards.
          </p>
        )}
      </div>

      {/* File input BEFORE (not inside) the dropzone button: nesting <input type="file"> inside
          role="button" triggers axe nested-interactive (WCAG 4.1.2) because AT can focus file
          inputs even with tabIndex=-1/aria-hidden when inside a button context.
          display:none + programmatic .click() is the standard pattern; the picker still opens. */}
      <input
        ref={fileRef}
        type="file"
        accept=".csv,text/csv"
        aria-label="CSV file"
        tabIndex={-1}
        style={{ display: 'none' }}
        onChange={(e) => {
          void handleChange(e);
        }}
      />
      <div
        className={['ob-dropzone', isPending ? 'ob-dropzone--loading' : '']
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
        {isPending ? (
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

      {(supersede ? supersedeMutation.isError : mutation.isError) && (
        <ApiErrorNotice
          error={supersede ? supersedeMutation.error : mutation.error}
          fallback="Import failed."
        />
      )}

      {isSuccess && (
        <div className="ob-success-banner" role="status">
          <Icon name="check" size={16} />
          <span>
            {supersede && counts
              ? counts.superseded === 0
                ? 'No figures differed — nothing was superseded.'
                : `${counts.superseded} corrected, ${counts.unchanged} unchanged.`
              : `Imported ${result.rowCount} row${result.rowCount !== 1 ? 's' : ''} successfully.`}
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

      {onContinue && (
        <div className="ob-step-actions row gap8 align-center mt16">
          <Button
            variant="primary"
            onClick={onContinue}
            disabled={!canContinue}
            aria-disabled={!canContinue}
          >
            Continue →
          </Button>
          {!canContinue && (
            <span className="fs12 muted">Import at least one balance type to continue.</span>
          )}
        </div>
      )}
    </Card>
  );
}

// ─── Convenience re-export ────────────────────────────────────────────────────

export type { EntityImportStepProps, BalanceImportStepProps };
