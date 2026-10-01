import { useMutation } from '@tanstack/react-query';
import {
  useEffect,
  useId,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent,
  type ReactNode,
} from 'react';
import type { ApiError, TenantDetail } from '@/api';
import { Button, EmptyState, Icon, Input, Money } from '@/design';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { ErrorAction } from '@/components/ErrorAction';
import { AudienceHint } from '@/components/InternalNote';
import { Modal } from '@/components/Modal';
import { num, useOwners, useProperties } from '@/lib/directory';
import { useBankAccounts } from '@/lib/settings';
import { trackInteraction, type BudgetTask } from '@/lib/telemetry';
import { LOCKED_PERIOD_MESSAGE } from '@/features/tenants/ledgerMutations';
import {
  amountInputValue,
  centsToAmount,
  issueRefundCheck,
  MAX_CHECK_NUMBER,
  newIssueKey,
  parseAmountCents,
  POSTAL_PATTERN,
  printRefundCheck,
  REFUND_CHECK_BUDGET,
  type RefundCheckFund,
  type RefundCheckView,
  sourceLabel,
  STATE_PATTERN,
  todayLocalIso,
  toCents,
  useRefundCheckOptions,
} from './refundChecks';
import './refundChecks.css';

// A closed enum published by the host (`BudgetTasks.All`); the client is typed from it.
const REFUND_CHECK_TASK: BudgetTask = 'issue-refund-check';

export interface RefundCheckModalProps {
  tenantId: string;
  /** The tenant header the ledger page already holds: payee name and the unit address prefill. */
  tenant: TenantDetail;
  onClose: () => void;
  /** The check is issued and posted: the page refreshes what it changed and flashes the entry. */
  onIssued: (check: RefundCheckView) => void;
}

type FieldName =
  | 'fund'
  | 'amount'
  | 'checkNumber'
  | 'date'
  | 'payeeName'
  | 'addressLine1'
  | 'addressLine2'
  | 'city'
  | 'state'
  | 'postalCode'
  | 'memo'
  | 'internalNote';

interface FormError extends ApiError {
  field?: FieldName;
}

const fundKey = (fund: RefundCheckFund) =>
  [fund.source, fund.bankAccountId, fund.propertyId ?? '', fund.ownerId ?? ''].join('|');

/**
 * Issues a refund check for a held security deposit or prepaid credit (#473). The server derives the
 * bank from the held liability, so the operator picks a *fund* (never a bank); every field arrives
 * prefilled — the held amount, the bank's next check number, today, the tenant's name and unit
 * address — so the common case is Issue then Print.
 *
 * One idempotency key is minted per open: a retried Issue returns the same check rather than writing a
 * second one. Once a check is issued the dialog only offers to print it or finish, never to issue again
 * under that key.
 */
export function RefundCheckModal({ tenantId, tenant, onClose, onIssued }: RefundCheckModalProps) {
  const fieldId = useId();
  const issueKey = useRef(newIssueKey());
  // The open is the first interaction; a fund choice is a committing choice (P59).
  const interactions = useRef(1);
  const budgetReported = useRef(false);

  const options = useRefundCheckOptions(tenantId);
  const banks = useBankAccounts(false);
  const funds = useMemo(() => options.data?.funds ?? [], [options.data]);
  const manyFunds = options.isSuccess && funds.length > 1;
  // Property and owner names only tell one deposit bucket from another, so they are read only when
  // there is more than one to tell apart.
  const properties = useProperties({ enabled: manyFunds && funds.some((f) => f.propertyId) });
  const owners = useOwners({ enabled: manyFunds && funds.some((f) => f.ownerId) });

  const [chosenKey, setChosenKey] = useState<string | null>(null);
  const [amount, setAmount] = useState('');
  const [checkNumber, setCheckNumber] = useState('');
  const [date, setDate] = useState(todayLocalIso);
  const [payeeName, setPayeeName] = useState(tenant.displayName);
  const [addressLine1, setAddressLine1] = useState(tenant.propertyAddress ?? '');
  const [addressLine2, setAddressLine2] = useState(
    tenant.propertyAddress && tenant.unitLabel ? tenant.unitLabel : '',
  );
  const [city, setCity] = useState('');
  const [state, setState] = useState('');
  const [postalCode, setPostalCode] = useState('');
  const [memo, setMemo] = useState('');
  const [internalNote, setInternalNote] = useState('');
  const [error, setError] = useState<FormError | null>(null);

  const [issued, setIssued] = useState<RefundCheckView | null>(null);
  const [printError, setPrintError] = useState<ApiError | null>(null);
  const [printing, setPrinting] = useState(false);
  const [printedOnce, setPrintedOnce] = useState(false);

  // The issued step replaces the form, and the field that held focus with it. Hand focus to Print, the
  // next step of the flow, rather than letting it fall to the page behind the dialog.
  const issuedId = issued?.id;
  useEffect(() => {
    if (issuedId) document.getElementById(`${fieldId}-print`)?.focus();
  }, [issuedId, fieldId]);

  const chosen = useMemo(() => {
    if (funds.length === 1) return funds[0];
    return funds.find((fund) => fundKey(fund) === chosenKey);
  }, [funds, chosenKey]);

  // Prefill from the fund: its held amount and its bank's next number. Re-runs when the operator picks
  // a different fund (a different bank has a different check sequence), never on a background refetch.
  const prefilledFor = useRef<string | null>(null);
  useEffect(() => {
    if (!chosen) return;
    const key = fundKey(chosen);
    if (prefilledFor.current === key) return;
    prefilledFor.current = key;
    setAmount(amountInputValue(chosen.held));
    setCheckNumber(chosen.nextCheckNumber == null ? '' : String(num(chosen.nextCheckNumber)));
  }, [chosen]);

  const bankName = (bankAccountId: string): string | null =>
    banks.isSuccess
      ? (banks.data.find((bank) => bank.id === bankAccountId)?.name ?? 'Unknown account')
      : null;

  /**
   * Issue posts money, so it waits for every read it depends on: the funds (amount, bucket and number
   * all come from them) and the bank names (which stock goes in the printer). A failed refetch blocks
   * too — stale funds may name a balance that has since been applied elsewhere.
   */
  const readsUnavailable = options.isPending || options.isError || banks.isPending || banks.isError;

  const mutation = useMutation<RefundCheckView, ApiError, RefundCheckFund>({
    mutationFn: (fund) => {
      const cents = parseAmountCents(amount) ?? 0;
      return issueRefundCheck({
        key: issueKey.current,
        tenantId,
        source: fund.source,
        amount: centsToAmount(cents),
        date,
        checkNumber: Number(checkNumber),
        payeeName: payeeName.trim(),
        addressLine1: addressLine1.trim(),
        addressLine2: addressLine2.trim() === '' ? null : addressLine2.trim(),
        city: city.trim(),
        state,
        postalCode: postalCode.trim(),
        memo: memo.trim() === '' ? null : memo.trim(),
        internalNote: internalNote.trim() === '' ? null : internalNote.trim(),
        bucket: {
          bankAccountId: fund.bankAccountId,
          propertyId: fund.propertyId,
          ownerId: fund.ownerId,
        },
      });
    },
    onSuccess: (check) => {
      interactions.current += 1; // Issue
      setIssued(check);
      onIssued(check);
    },
    onError: (err, fund) => {
      switch (err.code) {
        case 'check_number_taken':
          void options.refetch();
          setError({
            ...err,
            field: 'checkNumber',
            message: `Check #${checkNumber} has already been used on ${
              bankName(fund.bankAccountId) ?? 'this account'
            }. Enter the number printed on the next blank check in the printer.`,
          });
          break;
        case 'insufficient_liability':
          void options.refetch();
          setError({
            ...err,
            field: 'amount',
            message:
              'That is more than this fund holds now. The held amount has been refreshed — lower the amount to no more than it.',
          });
          break;
        case 'refund_bucket_ambiguous':
          void options.refetch();
          setError({ ...err, field: 'fund' });
          break;
        case 'refund_check_conflict':
          setError({
            ...err,
            message:
              'A check was already issued from this dialog with different details. Close it and find that check in the refund check list on Banking before issuing another.',
          });
          break;
        case 'account_period_locked':
          setError({ ...err, field: 'date', message: LOCKED_PERIOD_MESSAGE });
          break;
        case 'period_closed':
          setError({
            ...err,
            field: 'date',
            message: `${err.message} Date the check in an open period.`,
          });
          break;
        default:
          setError(err);
      }
    },
  });

  const fail = (field: FieldName, message: string) => {
    setError({ field, message });
    document.getElementById(`${fieldId}-${field}`)?.focus();
  };

  const submit = () => {
    if (mutation.isPending || issued) return;
    if (readsUnavailable) {
      setError({
        message:
          options.isPending || banks.isPending
            ? 'Still loading the held funds — try again in a moment.'
            : 'The held funds couldn’t be loaded, so no check can be issued yet.',
      });
      return;
    }
    if (!chosen) return fail('fund', 'Choose which fund to refund from.');
    const cents = parseAmountCents(amount);
    if (cents === null || cents <= 0) {
      return fail('amount', 'Enter an amount greater than zero, with at most two decimal places.');
    }
    if (cents > toCents(chosen.held)) {
      return fail('amount', 'The amount is more than this fund holds.');
    }
    const number = Number(checkNumber);
    if (!/^\d+$/.test(checkNumber.trim()) || number < 1 || number > MAX_CHECK_NUMBER) {
      return fail('checkNumber', 'Enter the check number printed on the blank check.');
    }
    if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) return fail('date', 'Enter the check date.');
    if (payeeName.trim() === '') return fail('payeeName', 'Enter who the check is payable to.');
    if (addressLine1.trim() === '') return fail('addressLine1', 'Enter the mailing address.');
    if (city.trim() === '') return fail('city', 'Enter the city.');
    if (!STATE_PATTERN.test(state)) return fail('state', 'Use the two-letter state code.');
    if (!POSTAL_PATTERN.test(postalCode.trim())) {
      return fail('postalCode', 'Use a 5-digit or ZIP+4 postal code.');
    }
    setError(null);
    mutation.mutate(chosen);
  };

  const print = async () => {
    // Guarded here rather than by `disabled`: disabling the focused button would drop focus.
    if (!issued || printing) return;
    setPrinting(true);
    setPrintError(null);
    try {
      await printRefundCheck(issued);
      setPrintedOnce(true);
      if (!budgetReported.current) {
        budgetReported.current = true;
        const count = interactions.current + 1; // Print
        trackInteraction(REFUND_CHECK_TASK, count, count <= REFUND_CHECK_BUDGET);
      }
    } catch (err) {
      setPrintError(err as ApiError);
    } finally {
      setPrinting(false);
    }
  };

  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      submit();
    }
  };

  const describedBy = (field: FieldName, ...more: (string | false)[]) =>
    [error?.field === field && `${fieldId}-error`, ...more].filter(Boolean).join(' ') || undefined;

  const field = (
    name: FieldName,
    label: string,
    input: {
      value: string;
      onChange: (value: string) => void;
      inputMode?: 'decimal' | 'numeric';
      type?: string;
      maxLength?: number;
      placeholder?: string;
      autoComplete?: string;
      hint?: { id: string; node: ReactNode };
    },
    className?: string,
  ) => (
    <div className={`col gap6${className ? ` ${className}` : ''}`}>
      <span className="pf-field-labelrow">
        <label className="pf-eyebrow" htmlFor={`${fieldId}-${name}`}>
          {label}
        </label>
        {input.hint?.node}
      </span>
      <Input
        id={`${fieldId}-${name}`}
        type={input.type}
        inputMode={input.inputMode}
        maxLength={input.maxLength}
        placeholder={input.placeholder}
        autoComplete={input.autoComplete}
        value={input.value}
        aria-invalid={error?.field === name || undefined}
        aria-describedby={describedBy(name, input.hint?.id ?? false)}
        onChange={(e) => input.onChange(e.target.value)}
        onKeyDown={onKeyDown}
      />
    </div>
  );

  // ---- issued: print or finish -------------------------------------------------------------------
  if (issued) {
    return (
      <Modal
        title="Refund check issued"
        onClose={onClose}
        footer={
          <>
            <Button variant="ghost" onClick={onClose}>
              Done
            </Button>
            <Button
              id={`${fieldId}-print`}
              variant="primary"
              icon="download"
              aria-busy={printing || undefined}
              onClick={() => void print()}
            >
              {printing ? 'Preparing…' : printedOnce ? 'Print again' : 'Print check'}
            </Button>
          </>
        }
      >
        <div className="pf-modal-body col gap12">
          <p className="row gap8 fs13" role="status">
            <Icon name="check" size={16} />
            <span>
              Check <b>#{num(issued.checkNumber)}</b> for <Money value={num(issued.amount)} plain />{' '}
              to {issued.payeeName} is issued and posted from{' '}
              <b>{bankName(issued.bankAccountId) ?? 'the trust account'}</b>.
            </span>
          </p>
          <p className="t3 fs13">
            Load check #{num(issued.checkNumber)} from that account’s stock in the printer, then
            print. Printing again keeps the same number; a misprint is voided from the refund check
            list on Banking and reissued on the next number.
          </p>
          {printedOnce && (
            <p className="t3 fs12 row gap6">
              <Icon name="check" size={14} /> Sent to the browser as a PDF.
            </p>
          )}
          <ApiErrorNotice error={printError} fallback="Couldn’t print the check." kind="read" />
        </div>
      </Modal>
    );
  }

  // ---- issue form ----------------------------------------------------------------------------------
  const nothingToRefund = options.isSuccess && funds.length === 0;
  const single = funds.length === 1 ? funds[0] : undefined;
  const fundLabel = (fund: RefundCheckFund) => {
    const extra: string[] = [];
    if (manyFunds && fund.propertyId) {
      extra.push(
        properties.isSuccess
          ? (properties.data.items.find((p) => p.id === fund.propertyId)?.address ??
              'Unknown property')
          : properties.isError
            ? 'Property name unavailable'
            : 'Loading property…',
      );
    }
    if (manyFunds && fund.ownerId) {
      extra.push(
        owners.isSuccess
          ? (owners.data.items.find((o) => o.id === fund.ownerId)?.name ?? 'Unknown owner')
          : owners.isError
            ? 'Owner name unavailable'
            : 'Loading owner…',
      );
    }
    return (
      <span className="pf-refund-fund-text">
        <span className="fw6">{sourceLabel(fund.source)}</span>
        <span className="t3 fs12">
          {bankName(fund.bankAccountId) ??
            (banks.isError ? 'Account name unavailable' : 'Loading account…')}
          {extra.length > 0 && ` · ${extra.join(' · ')}`}
        </span>
      </span>
    );
  };

  return (
    <Modal
      title="Refund check"
      onClose={onClose}
      footer={
        nothingToRefund ? (
          <Button variant="ghost" onClick={onClose}>
            Close
          </Button>
        ) : (
          <>
            <Button variant="ghost" onClick={onClose}>
              Cancel
            </Button>
            <Button
              variant="primary"
              icon="check"
              disabled={mutation.isPending || readsUnavailable}
              onClick={submit}
            >
              {mutation.isPending ? 'Issuing…' : 'Issue check'}
            </Button>
          </>
        )
      }
    >
      <div className="pf-modal-body col gap12">
        {/* Error before loading: a failed read must never sit behind a skeleton. */}
        {options.isError ? (
          <div className="col gap6">
            <ApiErrorNotice
              error={options.error}
              fallback="Couldn’t load the held funds."
              kind="read"
            />
            <ErrorAction
              error={options.error}
              onRetry={() => void options.refetch()}
              retrying={options.isFetching}
            />
          </div>
        ) : options.isPending ? (
          <div className="col gap8">
            <span className="t3 fs12">Loading the held funds…</span>
            <div className="pf-skeleton" style={{ height: 20 }} />
            <div className="pf-skeleton" style={{ height: 20 }} />
          </div>
        ) : nothingToRefund ? (
          <EmptyState
            icon="wallet"
            title="Nothing to refund"
            description="No held deposit or prepaid credit to refund."
          />
        ) : (
          <>
            {banks.isError && (
              <div className="col gap6">
                <ApiErrorNotice
                  error={banks.error}
                  fallback="Couldn’t load the trust account names."
                  kind="read"
                />
                <ErrorAction
                  error={banks.error}
                  onRetry={() => void banks.refetch()}
                  retrying={banks.isFetching}
                />
              </div>
            )}

            {single ? (
              <div className="col gap6">
                <span className="pf-eyebrow">Refund from</span>
                <div className="pf-refund-fund is-single">
                  {fundLabel(single)}
                  <span className="pf-refund-fund-held">
                    <span className="t3 fs12">Held</span> <Money value={num(single.held)} plain />
                  </span>
                </div>
              </div>
            ) : (
              <fieldset
                className="pf-refund-funds"
                aria-describedby={error?.field === 'fund' ? `${fieldId}-error` : undefined}
              >
                <legend className="pf-eyebrow">Refund from</legend>
                {funds.map((fund, index) => {
                  const key = fundKey(fund);
                  return (
                    <label key={key} className="pf-refund-fund">
                      <input
                        id={index === 0 ? `${fieldId}-fund` : undefined}
                        type="radio"
                        name={`${fieldId}-fund`}
                        value={key}
                        checked={chosenKey === key}
                        onChange={() => {
                          interactions.current += 1; // a committing choice
                          setChosenKey(key);
                          setError(null);
                        }}
                      />
                      {fundLabel(fund)}
                      <span className="pf-refund-fund-held">
                        <span className="t3 fs12">Held</span> <Money value={num(fund.held)} plain />
                      </span>
                    </label>
                  );
                })}
              </fieldset>
            )}

            {(properties.isError || owners.isError) && (
              <div className="col gap6">
                <ApiErrorNotice
                  error={properties.error ?? owners.error}
                  fallback="Couldn’t load the property and owner names for these funds."
                  kind="read"
                />
                <ErrorAction
                  error={properties.error ?? owners.error}
                  onRetry={() => {
                    if (properties.isError) void properties.refetch();
                    if (owners.isError) void owners.refetch();
                  }}
                  retrying={properties.isFetching || owners.isFetching}
                />
              </div>
            )}

            <div className="pf-refund-grid">
              {field('amount', 'Amount', {
                value: amount,
                onChange: (v) => setAmount(v.replace(/[^0-9.]/g, '')),
                inputMode: 'decimal',
                placeholder: '0.00',
              })}
              {field('checkNumber', 'Check number', {
                value: checkNumber,
                onChange: (v) => setCheckNumber(v.replace(/\D/g, '')),
                inputMode: 'numeric',
                maxLength: 8,
                hint:
                  chosen && chosen.nextCheckNumber == null
                    ? {
                        id: `${fieldId}-checkNumber-hint`,
                        node: (
                          <span id={`${fieldId}-checkNumber-hint`} className="pf-refund-hint">
                            First check on this account — enter the number on the blank in the
                            printer
                          </span>
                        ),
                      }
                    : undefined,
              })}
              {field('date', 'Check date', { value: date, onChange: setDate, type: 'date' })}
            </div>

            {field('payeeName', 'Pay to the order of', {
              value: payeeName,
              onChange: setPayeeName,
              maxLength: 120,
              autoComplete: 'off',
            })}
            {field('addressLine1', 'Mailing address', {
              value: addressLine1,
              onChange: setAddressLine1,
              maxLength: 120,
              autoComplete: 'off',
            })}
            {field('addressLine2', 'Address line 2', {
              value: addressLine2,
              onChange: setAddressLine2,
              maxLength: 120,
              autoComplete: 'off',
              placeholder: 'Optional',
            })}
            <div className="pf-refund-grid">
              {field('city', 'City', {
                value: city,
                onChange: setCity,
                maxLength: 60,
                autoComplete: 'off',
              })}
              {field('state', 'State', {
                value: state,
                onChange: (v) =>
                  setState(
                    v
                      .toUpperCase()
                      .replace(/[^A-Z]/g, '')
                      .slice(0, 2),
                  ),
                maxLength: 2,
                placeholder: 'NC',
                autoComplete: 'off',
              })}
              {field('postalCode', 'ZIP', {
                value: postalCode,
                onChange: (v) => setPostalCode(v.replace(/[^0-9-]/g, '').slice(0, 10)),
                inputMode: 'numeric',
                maxLength: 10,
                autoComplete: 'off',
              })}
            </div>

            {field('memo', 'Memo', {
              value: memo,
              onChange: setMemo,
              maxLength: 60,
              placeholder: 'e.g. Security deposit refund',
              hint: {
                id: `${fieldId}-memo-hint`,
                node: (
                  <span id={`${fieldId}-memo-hint`} className="pf-refund-hint">
                    <Icon name="eye" size={12} /> Prints on the check
                  </span>
                ),
              },
            })}
            {field('internalNote', 'Internal note', {
              value: internalNote,
              onChange: setInternalNote,
              maxLength: 500,
              placeholder: 'Optional',
              hint: {
                id: `${fieldId}-internalNote-hint`,
                node: <AudienceHint id={`${fieldId}-internalNote-hint`} audience="staff" />,
              },
            })}
          </>
        )}

        <div id={`${fieldId}-error`}>
          <ApiErrorNotice error={error} />
        </div>
      </div>
    </Modal>
  );
}
