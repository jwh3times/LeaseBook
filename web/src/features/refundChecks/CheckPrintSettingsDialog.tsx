import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useId, useState, type KeyboardEvent } from 'react';
import type { ApiError } from '@/api';
import { Button, Input } from '@/design';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { ErrorAction } from '@/components/ErrorAction';
import { Modal } from '@/components/Modal';
import { num } from '@/lib/directory';
import { BankMicrSection } from './BankMicrSection';
import {
  checkPrintSettingsKey,
  type CheckPrintSettingsView,
  OFFSET_LIMIT_POINTS,
  parseOffset,
  printAlignmentPage,
  saveCheckPrintSettings,
  useCheckPrintSettings,
} from './refundChecks';

export interface CheckPrintSettingsDialogProps {
  bankAccountId: string;
  bankName: string;
  onClose: () => void;
}

type Axis = 'x' | 'y';

const OUT_OF_RANGE = `Enter points from −${OFFSET_LIMIT_POINTS} to ${OFFSET_LIMIT_POINTS} (one inch either way), with at most two decimals.`;

/**
 * A bank account's check-stock calibration (#473): how far every printed field shifts, in points
 * (72 to the inch), to land on that account's pre-printed stock. The alignment page prints with the
 * saved offsets, so unsaved changes are saved first.
 */
export function CheckPrintSettingsDialog({
  bankAccountId,
  bankName,
  onClose,
}: CheckPrintSettingsDialogProps) {
  const fieldId = useId();
  const queryClient = useQueryClient();
  const settings = useCheckPrintSettings(bankAccountId);
  const [typedX, setX] = useState<string | null>(null);
  const [typedY, setY] = useState<string | null>(null);
  const [error, setError] = useState<(ApiError & { field?: Axis }) | null>(null);
  const [saved, setSaved] = useState(false);
  const [printing, setPrinting] = useState(false);
  const [printError, setPrintError] = useState<ApiError | null>(null);

  // A field shows what was typed into it, and until then the saved setting: a refetch never
  // overwrites what is being typed. Derived, not copied into state by an effect, so there is no
  // render in which the settings have loaded and the fields are still empty and disabled (#532).
  const x = typedX ?? (settings.isSuccess ? String(num(settings.data.offsetXPoints)) : null);
  const y = typedY ?? (settings.isSuccess ? String(num(settings.data.offsetYPoints)) : null);

  const readUnavailable = settings.isPending || settings.isError || x === null || y === null;
  const dirty =
    settings.isSuccess &&
    x !== null &&
    y !== null &&
    (parseOffset(x) !== num(settings.data.offsetXPoints) ||
      parseOffset(y) !== num(settings.data.offsetYPoints));

  const save = useMutation<CheckPrintSettingsView, ApiError, { x: number; y: number }>({
    mutationFn: ({ x: ox, y: oy }) => saveCheckPrintSettings(bankAccountId, ox, oy),
    onSuccess: (view) => {
      queryClient.setQueryData(checkPrintSettingsKey(bankAccountId), view);
      setSaved(true);
    },
    onError: (err) => setError(err),
  });

  /** Validates both fields; on failure reports the first and returns null. */
  const validated = (): { x: number; y: number } | null => {
    const ox = parseOffset(x ?? '');
    if (ox === null) {
      setError({ field: 'x', message: OUT_OF_RANGE });
      document.getElementById(`${fieldId}-x`)?.focus();
      return null;
    }
    const oy = parseOffset(y ?? '');
    if (oy === null) {
      setError({ field: 'y', message: OUT_OF_RANGE });
      document.getElementById(`${fieldId}-y`)?.focus();
      return null;
    }
    return { x: ox, y: oy };
  };

  const onSave = () => {
    if (readUnavailable || save.isPending) return;
    setSaved(false);
    const values = validated();
    if (!values) return;
    setError(null);
    save.mutate(values);
  };

  const onAlignment = async () => {
    if (readUnavailable || printing) return;
    setSaved(false);
    const values = validated();
    if (!values) return;
    setError(null);
    setPrintError(null);
    setPrinting(true);
    try {
      if (dirty) {
        try {
          await save.mutateAsync(values);
        } catch {
          return; // the mutation's onError already reported it
        }
      }
      await printAlignmentPage(bankAccountId);
    } catch (err) {
      setPrintError(err as ApiError);
    } finally {
      setPrinting(false);
    }
  };

  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      onSave();
    }
  };

  const offsetField = (axis: Axis, label: string, hint: string, value: string | null) => (
    <div className="col gap6">
      <label className="pf-eyebrow" htmlFor={`${fieldId}-${axis}`}>
        {label}
      </label>
      <Input
        id={`${fieldId}-${axis}`}
        inputMode="decimal"
        value={value ?? ''}
        disabled={value === null}
        aria-invalid={error?.field === axis || undefined}
        aria-describedby={[`${fieldId}-${axis}-hint`, error?.field === axis && `${fieldId}-error`]
          .filter(Boolean)
          .join(' ')}
        onChange={(e) => {
          setSaved(false);
          const next = e.target.value.replace(/[^0-9.\-−]/g, '');
          if (axis === 'x') setX(next);
          else setY(next);
        }}
        onKeyDown={onKeyDown}
      />
      <span id={`${fieldId}-${axis}-hint`} className="t3 fs12">
        {hint}
      </span>
    </div>
  );

  return (
    <Modal
      title="Check print settings"
      onClose={onClose}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            Close
          </Button>
          <Button
            variant="default"
            icon="download"
            disabled={readUnavailable || printing || save.isPending}
            onClick={() => void onAlignment()}
          >
            {printing ? 'Preparing…' : 'Print alignment page'}
          </Button>
          <Button
            variant="primary"
            icon="check"
            disabled={readUnavailable || save.isPending || printing}
            onClick={onSave}
          >
            {save.isPending ? 'Saving…' : 'Save'}
          </Button>
        </>
      }
    >
      <div className="pf-modal-body col gap12">
        <p className="t3 fs13">
          Shifts every printed field on <b style={{ color: 'var(--text)' }}>{bankName}</b> checks to
          line up with the pre-printed stock. Measured in points: 72 points is one inch, and each
          offset is limited to one inch either way. Print the alignment page on plain paper, hold it
          against a blank check, and adjust.
        </p>
        {settings.isError ? (
          <div className="col gap6">
            <ApiErrorNotice
              error={settings.error}
              fallback="Couldn’t load the print settings."
              kind="read"
            />
            <ErrorAction
              error={settings.error}
              onRetry={() => void settings.refetch()}
              retrying={settings.isFetching}
            />
          </div>
        ) : settings.isPending ? (
          <div className="pf-skeleton" style={{ height: 44 }} />
        ) : (
          <div className="pf-offset-grid">
            {offsetField(
              'x',
              'Horizontal offset (points)',
              'Positive moves right, negative moves left.',
              x,
            )}
            {offsetField(
              'y',
              'Vertical offset (points)',
              'Positive moves down, negative moves up.',
              y,
            )}
          </div>
        )}
        {saved && !dirty && (
          <p className="t3 fs12" role="status">
            Saved.
          </p>
        )}
        <div id={`${fieldId}-error`}>
          <ApiErrorNotice error={error} />
        </div>
        <ApiErrorNotice
          error={printError}
          fallback="Couldn’t print the alignment page."
          kind="read"
        />
        <BankMicrSection bankAccountId={bankAccountId} />
      </div>
    </Modal>
  );
}
