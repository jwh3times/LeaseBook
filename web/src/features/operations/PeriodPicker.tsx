/**
 * A compact year/month period picker for the operations run screens.
 * Renders two selects (year, month). No floating popover — operations screens have horizontal
 * space for inline controls. Keyboard-operable via native <select>.
 */
import { Select } from '@/design';

const MONTHS = [
  'January',
  'February',
  'March',
  'April',
  'May',
  'June',
  'July',
  'August',
  'September',
  'October',
  'November',
  'December',
];

/** How many past years + current year to offer. */
const YEAR_RANGE = 3;

function buildYears(): number[] {
  const current = new Date().getFullYear();
  return Array.from({ length: YEAR_RANGE + 1 }, (_, i) => current - YEAR_RANGE + i + 1);
}

export interface PeriodPickerProps {
  year: number;
  month: number;
  onChange: (year: number, month: number) => void;
}

export function PeriodPicker({ year, month, onChange }: PeriodPickerProps) {
  const years = buildYears();

  return (
    <div className="row gap8" style={{ alignItems: 'center' }}>
      <Select
        aria-label="Select year"
        value={year}
        onChange={(e) => onChange(Number(e.target.value), month)}
      >
        {years.map((y) => (
          <option key={y} value={y}>
            {y}
          </option>
        ))}
      </Select>
      <Select
        aria-label="Select month"
        value={month}
        onChange={(e) => onChange(year, Number(e.target.value))}
      >
        {MONTHS.map((label, i) => (
          <option key={i + 1} value={i + 1}>
            {label}
          </option>
        ))}
      </Select>
    </div>
  );
}
