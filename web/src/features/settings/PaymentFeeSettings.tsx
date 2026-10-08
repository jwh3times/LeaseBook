import { useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { getApiPayments, putApiSettingsPaymentFees, unwrap } from '@/api';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { Badge, Button, Card, CardHeader, Input } from '@/design';
import { useSession } from '@/features/auth/useSession';
import { orgSettingsKey, type OrgSettings } from '@/lib/settings';
import { useDraft } from '@/lib/useDraft';

type Method = 'card' | 'ach';
interface Rule {
  rate: string;
  fixed: string;
  cap: string;
}

const METHODS: { key: Method; label: string }[] = [
  { key: 'card', label: 'Card' },
  { key: 'ach', label: 'Bank debit (ACH)' },
];

// The rate is stored in basis points and entered as a percentage, as the late-fee rate is.
function read(initial: OrgSettings): Record<Method, Rule> {
  const rule = (bps: unknown, fixed: unknown, cap: unknown): Rule => ({
    rate: String(Number(bps ?? 0) / 100),
    fixed: Number(fixed ?? 0).toFixed(2),
    cap: cap == null ? '' : Number(cap).toFixed(2),
  });
  return {
    card: rule(initial.cardFeeRateBps, initial.cardFeeFixed, initial.cardFeeCap),
    ach: rule(initial.achFeeRateBps, initial.achFeeFixed, initial.achFeeCap),
  };
}

/**
 * The convenience-fee rule for each online payment method (ADR-053): a rate, a fixed amount and an
 * optional cap. The tenant pays the fee on top of the amount that goes to their ledger; the fee is
 * never in the ledger. Shown only where online payments exist, which today is the payment simulation.
 * Both rules are saved together, by an administrator.
 */
export function PaymentFeeSettings({ initial }: { initial: OrgSettings }) {
  const session = useSession();
  const queries = useQueryClient();
  const payments = useQuery({
    queryKey: ['simulated-payments', 'availability'],
    queryFn: () => unwrap(getApiPayments(), 'Unable to check online payments.'),
    retry: false,
    staleTime: 60_000,
  });
  const { value: rules, edit, discard } = useDraft(read(initial));
  const [saved, setSaved] = useState(false);
  const update = useMutation({
    mutationFn: () =>
      unwrap(
        putApiSettingsPaymentFees({
          body: {
            cardFeeRateBps: Math.round(Number(rules.card.rate) * 100),
            cardFeeFixed: Number(rules.card.fixed),
            cardFeeCap: rules.card.cap.trim() === '' ? null : Number(rules.card.cap),
            achFeeRateBps: Math.round(Number(rules.ach.rate) * 100),
            achFeeFixed: Number(rules.ach.fixed),
            achFeeCap: rules.ach.cap.trim() === '' ? null : Number(rules.ach.cap),
          },
        }),
        'Failed to save the payment fees',
      ),
    onSuccess: (data) => {
      queries.setQueryData(orgSettingsKey, data);
      discard();
      setSaved(true);
    },
  });
  // Absent, not empty, where there are no online payments: the rules would price nothing.
  if (!payments.data?.enabled) return null;
  const admin = session.data?.role === 'PMAdmin';

  function set(method: Method, field: keyof Rule, value: string) {
    edit((current) => ({ ...current, [method]: { ...current[method], [field]: value } }));
    setSaved(false);
  }
  function save(event: FormEvent) {
    event.preventDefault();
    update.mutate();
  }

  return (
    <Card>
      <CardHeader
        title="Online payment fees"
        sub="The convenience fee a tenant pays on top of the amount that goes to their ledger."
      />
      <form className="pf-pad col gap14" onSubmit={save}>
        {METHODS.map(({ key, label }) => (
          <fieldset key={key} className="row gap12 wrap" disabled={!admin || update.isPending}>
            <legend className="pf-eyebrow">{label}</legend>
            <div className="pf-formrow" style={{ width: 150 }}>
              <label htmlFor={`fee-${key}-rate`}>Rate (%)</label>
              <Input
                id={`fee-${key}-rate`}
                type="number"
                min={0}
                max={20}
                step={0.01}
                required
                aria-describedby={`fee-${key}-rate-hint`}
                className="pf-num"
                value={rules[key].rate}
                onChange={(e) => set(key, 'rate', e.target.value)}
              />
              <span id={`fee-${key}-rate-hint`} className="t3 fs12">
                0 to 20.
              </span>
            </div>
            <div className="pf-formrow" style={{ width: 150 }}>
              <label htmlFor={`fee-${key}-fixed`}>Fixed amount ($)</label>
              <Input
                id={`fee-${key}-fixed`}
                type="number"
                min={0}
                step={0.01}
                required
                className="pf-num"
                value={rules[key].fixed}
                onChange={(e) => set(key, 'fixed', e.target.value)}
              />
            </div>
            <div className="pf-formrow" style={{ width: 150 }}>
              <label htmlFor={`fee-${key}-cap`}>Cap ($)</label>
              <Input
                id={`fee-${key}-cap`}
                type="number"
                min={0.01}
                step={0.01}
                className="pf-num"
                value={rules[key].cap}
                aria-describedby={`fee-${key}-cap-hint`}
                onChange={(e) => set(key, 'cap', e.target.value)}
              />
              <span id={`fee-${key}-cap-hint`} className="t3 fs12">
                Leave empty for no cap.
              </span>
            </div>
          </fieldset>
        ))}
        <p className="t3 fs13">
          The fee is the rate times the amount charged, plus the fixed amount, up to the cap. The
          tenant sees it before confirming a payment. A payment already requested keeps the fee it
          was quoted. Zero in both fields charges nothing.
        </p>
        <div className="row gap12">
          {admin ? (
            <Button type="submit" variant="primary" size="sm" disabled={update.isPending}>
              {update.isPending ? 'Saving…' : 'Save payment fees'}
            </Button>
          ) : (
            <span className="t3 fs13">Only an administrator can change these fees.</span>
          )}
          {saved && (
            <Badge tone="pos" dot>
              Saved
            </Badge>
          )}
          <ApiErrorNotice error={update.error} fallback="Couldn’t save the payment fees." />
        </div>
      </form>
    </Card>
  );
}
