import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useState } from 'react';
import type { ApiError } from '@/api';
import { Button, Input } from '@/design';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { ErrorAction } from '@/components/ErrorAction';
import { useSession } from '@/features/auth/useSession';
import { num } from '@/lib/directory';
import {
  bankMicrKey,
  type BankMicrDetailsView,
  MICR_OFFSET_LIMIT_POINTS,
  parseOffset,
  saveBankMicrDetails,
  useBankMicrDetails,
} from './refundChecks';

type StockKind = 'preprinted' | 'blank';

const STOCK_LABEL: Record<StockKind, string> = {
  preprinted: 'Pre-printed stock — the bank’s MICR line is already on it',
  blank: 'Blank stock — LeaseBook prints the MICR line',
};

const OFFSET_OUT_OF_RANGE = `Enter points from −${MICR_OFFSET_LIMIT_POINTS} to ${MICR_OFFSET_LIMIT_POINTS} (a quarter inch either way), with at most two decimals.`;

function stockKindOf(view: BankMicrDetailsView): StockKind {
  return view.stockKind === 'blank' ? 'blank' : 'preprinted';
}

export interface BankMicrSectionProps {
  bankAccountId: string;
}

/**
 * A bank account's MICR details (#474): what its checks are printed on, the routing number and the
 * bank's On-Us field, and how far the MICR line sits from its nominal position. The numbers are
 * write-only — the server shows their last four digits and nothing here keeps them after a save — and
 * only an administrator may change any of it; staff see the same masked summary read-only.
 */
export function BankMicrSection({ bankAccountId }: BankMicrSectionProps) {
  const fieldId = useId();
  const queryClient = useQueryClient();
  const session = useSession();
  const details = useBankMicrDetails(bankAccountId);
  const isAdmin = session.isSuccess && session.data?.role === 'PMAdmin';

  const [stockKind, setStockKind] = useState<StockKind | null>(null);
  const [routing, setRouting] = useState('');
  const [onUs, setOnUs] = useState('');
  const [x, setX] = useState<string | null>(null);
  const [y, setY] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);

  // Seed the editable values from the saved details once; a refetch never overwrites what is being typed.
  useEffect(() => {
    if (!details.isSuccess) return;
    setStockKind((current) => current ?? stockKindOf(details.data));
    setX((current) => current ?? String(num(details.data.micrOffsetXPoints)));
    setY((current) => current ?? String(num(details.data.micrOffsetYPoints)));
  }, [details.isSuccess, details.data]);

  const onSave = async () => {
    if (!details.isSuccess || stockKind === null || saving) return;
    const ox = parseOffset(x ?? '', MICR_OFFSET_LIMIT_POINTS);
    const oy = parseOffset(y ?? '', MICR_OFFSET_LIMIT_POINTS);
    if (ox === null || oy === null) {
      setError({ message: OFFSET_OUT_OF_RANGE });
      return;
    }
    setSaved(false);
    setError(null);
    setSaving(true);
    try {
      const view = await saveBankMicrDetails(bankAccountId, {
        stockKind,
        // An empty field keeps the saved number: the form never holds it to send back.
        routingNumber: routing.trim() === '' ? null : routing.trim(),
        onUsAccountNumber: onUs === '' ? null : onUs,
        micrOffsetXPoints: ox,
        micrOffsetYPoints: oy,
      });
      queryClient.setQueryData(bankMicrKey(bankAccountId), view);
      setRouting('');
      setOnUs('');
      setSaved(true);
    } catch (err) {
      setError(err as ApiError);
    } finally {
      setSaving(false);
    }
  };

  const offsetField = (
    axis: 'x' | 'y',
    label: string,
    value: string | null,
    set: (v: string) => void,
  ) => (
    <div className="col gap6">
      <label className="pf-eyebrow" htmlFor={`${fieldId}-${axis}`}>
        {label}
      </label>
      <Input
        id={`${fieldId}-${axis}`}
        inputMode="decimal"
        value={value ?? ''}
        disabled={value === null}
        onChange={(e) => {
          setSaved(false);
          set(e.target.value.replace(/[^0-9.\-−]/g, ''));
        }}
      />
    </div>
  );

  return (
    <fieldset className="pf-micr col gap12">
      <legend className="pf-eyebrow">MICR details</legend>
      {details.isError ? (
        <div className="col gap6">
          <ApiErrorNotice
            error={details.error}
            fallback="Couldn’t load the MICR details."
            kind="read"
          />
          <ErrorAction
            error={details.error}
            onRetry={() => void details.refetch()}
            retrying={details.isFetching}
          />
        </div>
      ) : details.isPending ? (
        <div className="pf-skeleton" style={{ height: 44 }} />
      ) : (
        <>
          <ul className="pf-micr-summary t2 fs13">
            <li>{STOCK_LABEL[stockKindOf(details.data)]}</li>
            <li>
              {details.data.routingNumberLast4
                ? `Routing number ending ${details.data.routingNumberLast4}`
                : 'No routing number saved'}
            </li>
            <li>
              {details.data.onUsAccountNumberLast4
                ? `On-Us field ending ${details.data.onUsAccountNumberLast4}`
                : 'No On-Us field saved'}
            </li>
          </ul>

          {!isAdmin ? (
            <p className="t3 fs12">Only an administrator can change the MICR details.</p>
          ) : (
            <>
              <fieldset className="pf-micr-stock col gap6">
                <legend className="pf-eyebrow">Check stock</legend>
                {(['preprinted', 'blank'] as const).map((kind) => (
                  <label key={kind} className="row gap6">
                    <input
                      type="radio"
                      name={`${fieldId}-stock`}
                      checked={stockKind === kind}
                      onChange={() => {
                        setSaved(false);
                        setStockKind(kind);
                      }}
                    />
                    {STOCK_LABEL[kind]}
                  </label>
                ))}
              </fieldset>
              <div className="pf-offset-grid">
                <div className="col gap6">
                  <label className="pf-eyebrow" htmlFor={`${fieldId}-routing`}>
                    Routing number
                  </label>
                  <Input
                    id={`${fieldId}-routing`}
                    inputMode="numeric"
                    autoComplete="off"
                    placeholder="Nine digits — leave empty to keep"
                    value={routing}
                    onChange={(e) => {
                      setSaved(false);
                      setRouting(e.target.value.replace(/\D/g, '').slice(0, 9));
                    }}
                  />
                </div>
                <div className="col gap6">
                  <label className="pf-eyebrow" htmlFor={`${fieldId}-onus`}>
                    On-Us field
                  </label>
                  <Input
                    id={`${fieldId}-onus`}
                    autoComplete="off"
                    placeholder="As your bank’s MICR sheet prints it — leave empty to keep"
                    aria-describedby={`${fieldId}-onus-hint`}
                    value={onUs}
                    onChange={(e) => {
                      setSaved(false);
                      setOnUs(e.target.value.toUpperCase().slice(0, 18));
                    }}
                  />
                  <span id={`${fieldId}-onus-hint`} className="t3 fs12">
                    Digits, a dash, spaces, and U for the On-Us symbol (⑈).
                  </span>
                </div>
                {offsetField('x', 'MICR line horizontal offset (points)', x, setX)}
                {offsetField('y', 'MICR line vertical offset (points)', y, setY)}
              </div>
              <div className="row gap8">
                <Button
                  variant="primary"
                  icon="check"
                  disabled={saving || stockKind === null}
                  onClick={() => void onSave()}
                >
                  {saving ? 'Saving…' : 'Save MICR details'}
                </Button>
                {saved && (
                  <span className="t3 fs12" role="status">
                    Saved.
                  </span>
                )}
              </div>
              <ApiErrorNotice error={error} />
            </>
          )}
        </>
      )}
    </fieldset>
  );
}
