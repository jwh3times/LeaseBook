import { useMemo, useState } from 'react';
import { asApiError, type ApiError } from '@/api';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { ErrorAction } from '@/components/ErrorAction';
import { Modal } from '@/components/Modal';
import { Button, Input, Select } from '@/design';
import { num, useCreateLease, useProperties, useUnits, type UnitRow } from '@/lib/directory';

/** A unit as the picker names it: occupancy today, then availability, in words, never by colour. */
function unitOptionLabel(unit: UnitRow): string {
  const parts = [unit.label, unit.occupancy === 'occupied' ? 'occupied' : 'vacant'];
  if (unit.availability === 'unavailable') parts.push('unavailable');
  return parts.join(' · ');
}

/** Vacant and available units first, the rest after; each group stays in label order. */
function pickerOrder(units: UnitRow[]): UnitRow[] {
  const open = (unit: UnitRow) =>
    unit.occupancy !== 'occupied' && unit.availability !== 'unavailable';
  return [...units.filter(open), ...units.filter((unit) => !open(unit))];
}

/**
 * Gives a tenant a lease without an import (#781). The property, then one of its units, then the
 * term, the lease rent — prefilled from the unit's asking rent — the deposit required and the lease
 * lifecycle status, active or pending. An occupied or unavailable unit stays choosable: the server's
 * overlap check, not the picker, decides whether the term is allowed.
 *
 * Creating a lease posts nothing. The deposit is only recorded here; collecting it stays the ledger's
 * deposit action, and late-fee overrides are set afterwards with Late fees.
 */
export function AddLeaseDialog({
  tenantId,
  tenantName,
  onClose,
}: {
  tenantId: string;
  tenantName: string;
  onClose: () => void;
}) {
  const create = useCreateLease(tenantId);
  const properties = useProperties();
  const [propertyId, setPropertyId] = useState('');
  const units = useUnits(propertyId);
  const [unitId, setUnitId] = useState('');
  const [startDate, setStartDate] = useState('');
  const [endDate, setEndDate] = useState('');
  const [rent, setRent] = useState('');
  const [deposit, setDeposit] = useState('');
  const [status, setStatus] = useState('active');
  const [error, setError] = useState<ApiError | null>(null);

  const propertyChoices = useMemo(
    () =>
      properties.isSuccess
        ? [...properties.data.items].sort((a, b) => a.address.localeCompare(b.address))
        : [],
    [properties.isSuccess, properties.data],
  );
  const property = propertyChoices.find((choice) => choice.id === propertyId);
  // Until the properties are loaded there is nothing to choose, and with none there is nothing to
  // lease: the dialog says which, and the save stays disabled rather than ignoring a press.
  const nothingToLeaseIn = propertyChoices.length === 0;
  const unitChoices = useMemo(
    () => (units.isSuccess ? pickerOrder(units.data) : []),
    [units.isSuccess, units.data],
  );

  // Choosing another property un-chooses the unit, and with it the asking rent that unit filled in.
  function chooseProperty(id: string) {
    setPropertyId(id);
    setUnitId('');
    setRent('');
  }

  function chooseUnit(id: string) {
    setUnitId(id);
    const chosen = unitChoices.find((choice) => choice.id === id);
    if (chosen) setRent(String(num(chosen.rent)));
  }

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    if (!property || create.isPending) return;
    setError(null);
    try {
      await create.mutateAsync({
        property,
        lease: {
          tenantId,
          unitId,
          startDate,
          endDate: endDate === '' ? null : endDate,
          rent: Number(rent),
          depositRequired: Number(deposit),
          status,
        },
      });
      onClose();
    } catch (e) {
      setError(asApiError(e, 'Could not add the lease. Check the fields and try again.'));
    }
  }

  return (
    <Modal
      title={`Add a lease for ${tenantName}`}
      onClose={onClose}
      onSubmit={submit}
      footer={
        <>
          <Button variant="ghost" size="sm" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="primary"
            size="sm"
            type="submit"
            disabled={create.isPending || nothingToLeaseIn}
          >
            {create.isPending ? 'Saving…' : 'Add lease'}
          </Button>
        </>
      }
    >
      <div className="pf-modal-body">
        {properties.isError ? (
          <div className="col gap6">
            <ApiErrorNotice
              error={properties.error}
              fallback="Couldn’t load the properties."
              kind="read"
            />
            <ErrorAction
              error={properties.error}
              onRetry={() => void properties.refetch()}
              retrying={properties.isFetching}
            />
          </div>
        ) : properties.isPending ? (
          <div className="col gap6">
            <span className="t3 fs12" role="status">
              Loading the properties…
            </span>
            <div className="pf-skeleton" style={{ height: 20 }} />
          </div>
        ) : propertyChoices.length === 0 ? (
          <p className="t3 fs13" role="status">
            No properties yet. Add a property and its units from the Properties page first.
          </p>
        ) : (
          <>
            <div className="row gap12">
              <div className="pf-formrow grow">
                <label htmlFor="l-property">Property</label>
                <Select
                  id="l-property"
                  value={propertyId}
                  onChange={(e) => chooseProperty(e.target.value)}
                  required
                >
                  <option value="">Choose a property</option>
                  {propertyChoices.map((choice) => (
                    <option key={choice.id} value={choice.id}>
                      {choice.address}
                    </option>
                  ))}
                </Select>
              </div>
              <div className="pf-formrow grow">
                <label htmlFor="l-unit">Unit</label>
                <Select
                  id="l-unit"
                  value={unitId}
                  onChange={(e) => chooseUnit(e.target.value)}
                  // Never disabled: a disabled field skips `required`, and a unit must be chosen.
                  // While the units load, or when there are none, only the prompt is offered.
                  required
                >
                  <option value="">Choose a unit</option>
                  {unitChoices.map((choice) => (
                    <option key={choice.id} value={choice.id}>
                      {unitOptionLabel(choice)}
                    </option>
                  ))}
                </Select>
              </div>
            </div>
            {propertyId !== '' &&
              (units.isError ? (
                <div className="col gap6">
                  <ApiErrorNotice
                    error={units.error}
                    fallback="Couldn’t load this property’s units."
                    kind="read"
                  />
                  <ErrorAction
                    error={units.error}
                    onRetry={() => void units.refetch()}
                    retrying={units.isFetching}
                  />
                </div>
              ) : units.isPending ? (
                <div className="col gap6">
                  <span className="t3 fs12" role="status">
                    Loading the units…
                  </span>
                  <div className="pf-skeleton" style={{ height: 20 }} />
                </div>
              ) : unitChoices.length === 0 ? (
                <p className="t3 fs13" role="status">
                  {property?.address} has no units yet. Add one from its property page first.
                </p>
              ) : null)}
          </>
        )}
        <div className="row gap12">
          <div className="pf-formrow grow">
            <label htmlFor="l-start">Start date</label>
            <Input
              id="l-start"
              type="date"
              value={startDate}
              onChange={(e) => setStartDate(e.target.value)}
              required
            />
          </div>
          <div className="pf-formrow grow">
            <label htmlFor="l-end">End date</label>
            <Input
              id="l-end"
              type="date"
              value={endDate}
              min={startDate || undefined}
              onChange={(e) => setEndDate(e.target.value)}
              aria-describedby="l-end-hint"
            />
            <span id="l-end-hint" className="t3 fs12">
              Leave blank for a month-to-month lease.
            </span>
          </div>
        </div>
        <div className="row gap12">
          <div className="pf-formrow grow">
            <label htmlFor="l-rent">Lease rent</label>
            <Input
              id="l-rent"
              type="number"
              min="0"
              step="0.01"
              value={rent}
              onChange={(e) => setRent(e.target.value)}
              required
            />
          </div>
          <div className="pf-formrow grow">
            <label htmlFor="l-deposit">Deposit required</label>
            <Input
              id="l-deposit"
              type="number"
              min="0"
              step="0.01"
              value={deposit}
              onChange={(e) => setDeposit(e.target.value)}
              aria-describedby="l-deposit-hint"
              required
            />
            <span id="l-deposit-hint" className="t3 fs12">
              Recorded only. Collect it with the ledger’s deposit action.
            </span>
          </div>
        </div>
        <div className="pf-formrow">
          <label htmlFor="l-status">Lifecycle</label>
          <Select id="l-status" value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="active">Active</option>
            <option value="pending">Pending</option>
          </Select>
        </div>
        <p className="t3 fs12">
          This page shows the lease in effect today. A pending lease, or one that starts later,
          shows here once it is in effect.
        </p>
        {error && <ApiErrorNotice error={error} fallback="Could not add the lease." />}
      </div>
    </Modal>
  );
}
