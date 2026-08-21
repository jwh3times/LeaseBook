import { useState } from 'react';
import { Button, Icon } from '@/design';

// Shared builder-strip chips for the reports feature. Extracted so both the generic ReportCatalog
// builder and the WP-8 CompliancePackPanel can reuse them without a circular import.

// ---- Filter chip (button that toggles a popover) ----------------------------

interface FilterChipBuilderProps {
  label: string;
  value: string;
  active?: boolean;
  onClick?: () => void;
}

export function FilterChipBuilder({ label, value, active, onClick }: FilterChipBuilderProps) {
  return (
    <button
      className={`pf-fchip${active ? ' active' : ''}`}
      aria-pressed={active}
      onClick={onClick}
      type="button"
    >
      <span className="pf-fchip-label">{label}</span>
      <span className="pf-fchip-value">{value}</span>
      <Icon name="chevronDown" size={13} />
    </button>
  );
}

// ---- SelectChip: a chip that opens a dropdown of options --------------------

export interface SelectChipOption {
  id: string;
  label: string;
}

interface SelectChipProps {
  label: string;
  value: string;
  options: SelectChipOption[];
  loading?: boolean;
  onSelect: (id: string | null) => void;
}

export function SelectChip({ label, value, options, loading, onSelect }: SelectChipProps) {
  const [open, setOpen] = useState(false);
  return (
    <div className="pf-filter-wrap">
      <FilterChipBuilder
        label={label}
        value={value}
        active={open}
        onClick={() => setOpen((v) => !v)}
      />
      {open && (
        <div className="pf-filter-popover" role="dialog" aria-label={`Select ${label}`}>
          {loading ? (
            <div className="t3 fs12" style={{ padding: 4 }}>
              Loading…
            </div>
          ) : (
            <div className="col gap4">
              <button
                className={`pf-basis-btn${value === 'All' ? ' active' : ''}`}
                type="button"
                onClick={() => {
                  onSelect(null);
                  setOpen(false);
                }}
              >
                All
              </button>
              {options.map((opt) => (
                <button
                  key={opt.id}
                  className={`pf-basis-btn${value === opt.label ? ' active' : ''}`}
                  type="button"
                  onClick={() => {
                    onSelect(opt.id);
                    setOpen(false);
                  }}
                >
                  {opt.label}
                </button>
              ))}
            </div>
          )}
          <div style={{ marginTop: 8, textAlign: 'right' }}>
            <Button variant="primary" onClick={() => setOpen(false)}>
              Done
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}

interface BasisChipProps {
  value: 'cash' | 'accrual';
  onSelect: (basis: 'cash' | 'accrual') => void;
}

/**
 * Accounting-basis chip. Deliberately not a {@link SelectChip}: that control always offers "All",
 * which is meaningless for a basis — a figure is on one basis or the other. Mirrors the owner
 * statement's basis toggle (`pf-basis-btn` + `aria-pressed`), the one basis control that has always
 * worked end to end.
 */
export function BasisChip({ value, onSelect }: BasisChipProps) {
  const [open, setOpen] = useState(false);
  return (
    <div className="pf-filter-wrap">
      <FilterChipBuilder
        label="Basis"
        value={value === 'accrual' ? 'Accrual' : 'Cash'}
        active={open}
        onClick={() => setOpen((v) => !v)}
      />
      {open && (
        <div className="pf-filter-popover" role="dialog" aria-label="Select Basis">
          <div className="row gap6">
            {(['cash', 'accrual'] as const).map((b) => (
              <button
                key={b}
                className={`pf-basis-btn${value === b ? ' active' : ''}`}
                type="button"
                aria-pressed={value === b}
                onClick={() => {
                  onSelect(b);
                  setOpen(false);
                }}
              >
                {b === 'cash' ? 'Cash' : 'Accrual'}
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
