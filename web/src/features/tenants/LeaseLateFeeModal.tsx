import { useState } from 'react';
import { Badge, Button, Input, Select } from '@/design';
import { Modal } from '@/components/Modal';
import { type TenantDetail, useUpdateLease } from '@/lib/directory';
import { useOrgSettings } from '@/lib/settings';

/**
 * The generated client types money and integers as `number | string` (OpenAPI decimals), so every
 * value crossing this boundary is coerced. Null is preserved deliberately — it is the "inherit"
 * state, not a missing number.
 */
function numOrNull(value: number | string | null | undefined): number | null {
  return value === null || value === undefined ? null : Number(value);
}

/**
 * Per-lease late-fee overrides (WP-6).
 *
 * Each of the five fields is independently either inherited or overridden — null means "use the org
 * default" for that field alone, so a lease can override the grace period while still inheriting the
 * fee amount. That is why every row is a two-part control: an inherit/override select, and a value
 * input that only appears once the field is overridden. A bare number input could not express the
 * difference between "inherit" and "explicitly zero", and both are legal.
 *
 * The org default is shown next to the inherit option so the operator can see what they are
 * inheriting without leaving the dialog.
 */
export function LeaseLateFeeModal({
  tenantId,
  detail,
  onClose,
}: {
  tenantId: string;
  detail: TenantDetail;
  onClose: () => void;
}) {
  const lease = detail.lease;
  const settings = useOrgSettings();
  const update = useUpdateLease(tenantId);

  const [dueDay, setDueDay] = useState(numOrNull(lease?.lateFeeRentDueDayOverride));
  const [grace, setGrace] = useState(numOrNull(lease?.lateFeeGraceDaysOverride));
  const [kind, setKind] = useState<string | null>(lease?.lateFeeKindOverride ?? null);
  const [amount, setAmount] = useState(numOrNull(lease?.lateFeeAmountOverride));
  const [rateBps, setRateBps] = useState(numOrNull(lease?.lateFeeRateBpsOverride));

  if (!lease) return null;

  const org = settings.data;

  async function save() {
    if (!lease) return;
    await update.mutateAsync({
      id: lease.id,
      // UpdateLease replaces the whole lease, so the untouched fields ride along unchanged.
      tenantId: detail.id,
      unitId: lease.unitId,
      startDate: lease.startDate ?? null,
      endDate: lease.endDate ?? null,
      rent: lease.rent,
      depositRequired: lease.depositRequired,
      status: lease.status,

      lateFeeRentDueDayOverride: dueDay,
      lateFeeGraceDaysOverride: grace,
      lateFeeKindOverride: kind,
      lateFeeAmountOverride: amount,
      lateFeeRateBpsOverride: rateBps,
    });
    onClose();
  }

  return (
    <Modal title="Late fee policy for this lease" onClose={onClose}>
      <div className="col gap14 pf-pad">
        <p className="t3 fs13">
          Each field either inherits the organization default or overrides it for this lease alone.
          The NC §42-46 cap still applies to whatever is configured here.
        </p>

        <OverrideRow
          id="lf-o-due"
          label="Rent due day"
          inheritedLabel={org ? `Day ${org.rentDueDay}` : 'org default'}
          value={dueDay}
          onChange={setDueDay}
          defaultWhenOverriding={Number(org?.rentDueDay ?? 1)}
          render={(value, onValue) => (
            <Input
              id="lf-o-due"
              type="number"
              min={1}
              max={28}
              className="pf-num"
              value={value}
              onChange={(e) => onValue(Number(e.target.value))}
            />
          )}
        />

        <OverrideRow
          id="lf-o-grace"
          label="Grace days"
          inheritedLabel={org ? `${org.lateFeeGraceDays} days` : 'org default'}
          value={grace}
          onChange={setGrace}
          defaultWhenOverriding={Number(org?.lateFeeGraceDays ?? 0)}
          render={(value, onValue) => (
            <Input
              id="lf-o-grace"
              type="number"
              min={0}
              className="pf-num"
              value={value}
              onChange={(e) => onValue(Number(e.target.value))}
            />
          )}
        />

        <div className="pf-formrow">
          <label htmlFor="lf-o-kind">Fee type</label>
          <Select
            id="lf-o-kind"
            value={kind ?? 'inherit'}
            onChange={(e) => setKind(e.target.value === 'inherit' ? null : e.target.value)}
          >
            <option value="inherit">
              Inherit{org ? ` (${org.lateFeeKind === 'percent' ? 'percent of rent' : 'flat'})` : ''}
            </option>
            <option value="flat">Flat amount</option>
            <option value="percent">Percent of rent</option>
          </Select>
        </div>

        <OverrideRow
          id="lf-o-amount"
          label="Flat fee"
          inheritedLabel={org ? `$${Number(org.lateFeeAmount).toFixed(2)}` : 'org default'}
          value={amount}
          onChange={setAmount}
          defaultWhenOverriding={Number(org?.lateFeeAmount ?? 0)}
          render={(value, onValue) => (
            <Input
              id="lf-o-amount"
              type="number"
              min={0}
              step={0.01}
              className="pf-num"
              value={value}
              onChange={(e) => onValue(Number(e.target.value))}
            />
          )}
        />

        <OverrideRow
          id="lf-o-rate"
          label="Rate (%)"
          inheritedLabel={org ? `${Number(org.lateFeeRateBps) / 100}%` : 'org default'}
          value={rateBps}
          onChange={setRateBps}
          defaultWhenOverriding={Number(org?.lateFeeRateBps ?? 0)}
          render={(value, onValue) => (
            <Input
              id="lf-o-rate"
              type="number"
              min={0}
              max={100}
              step={0.01}
              className="pf-num"
              // Stored in basis points, entered as a percentage.
              value={Number(value) / 100}
              onChange={(e) => onValue(Math.round(Number(e.target.value) * 100))}
            />
          )}
        />

        <div className="row gap12">
          <Button variant="primary" size="sm" onClick={save} disabled={update.isPending}>
            {update.isPending ? 'Saving…' : 'Save overrides'}
          </Button>
          <Button variant="ghost" size="sm" onClick={onClose}>
            Cancel
          </Button>
          {update.isError && <span className="err">Couldn’t save the lease overrides.</span>}
        </div>
      </div>
    </Modal>
  );
}

/**
 * One inherit-or-override field. `null` is inherit; any other value is an explicit override — which
 * is why the toggle is a select rather than a checkbox next to a number: it keeps "inherit" and
 * "zero" visibly distinct states rather than two readings of an empty box.
 */
function OverrideRow<T extends number>({
  id,
  label,
  inheritedLabel,
  value,
  onChange,
  defaultWhenOverriding,
  render,
}: {
  id: string;
  label: string;
  inheritedLabel: string;
  value: T | null;
  onChange: (next: T | null) => void;
  defaultWhenOverriding: number;
  render: (value: T, onValue: (next: number) => void) => React.ReactNode;
}) {
  const overriding = value !== null;
  return (
    <div className="row gap12 wrap">
      <div className="pf-formrow grow">
        <label htmlFor={`${id}-mode`}>{label}</label>
        <Select
          id={`${id}-mode`}
          value={overriding ? 'override' : 'inherit'}
          onChange={(e) =>
            onChange(e.target.value === 'inherit' ? null : (defaultWhenOverriding as T))
          }
        >
          <option value="inherit">Inherit ({inheritedLabel})</option>
          <option value="override">Override</option>
        </Select>
      </div>
      {overriding && (
        <div className="pf-formrow" style={{ width: 140 }}>
          <label htmlFor={id}>Value</label>
          {render(value, (next) => onChange(next as T))}
        </div>
      )}
    </div>
  );
}

/** Compact indicator for the ledger header: does this lease deviate from the org policy? */
export function LeaseLateFeeBadge({ lease }: { lease: TenantDetail['lease'] }) {
  if (!lease) return null;
  const overridden =
    lease.lateFeeRentDueDayOverride !== null ||
    lease.lateFeeGraceDaysOverride !== null ||
    lease.lateFeeKindOverride !== null ||
    lease.lateFeeAmountOverride !== null ||
    lease.lateFeeRateBpsOverride !== null;

  return overridden ? (
    <Badge tone="warn" dot>
      Custom late fees
    </Badge>
  ) : null;
}
