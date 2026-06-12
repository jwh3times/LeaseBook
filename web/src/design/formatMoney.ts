// Money formatting — mirrors the prototype's fmt / fmtPlain / fmtK (private/claude_design_files).
// All money in the UI flows through here or <Money>; never ad-hoc toFixed (§C.2). Uses the real
// Unicode minus sign (U+2212) to align with tabular numerals.

export type NegativeStyle = 'minus' | 'parens';

export interface FormatMoneyOptions {
  /** Render a leading '+' on positive values. */
  sign?: boolean;
  /** Render an em-dash for exactly zero (default true). */
  dash?: boolean;
  /** How negatives render — minus sign (default) or accounting parentheses. */
  negativeStyle?: NegativeStyle;
}

const MINUS = '−';
const EM_DASH = '—';

function abs2(value: number): string {
  return Math.abs(value).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

export function formatMoney(value: number, options: FormatMoneyOptions = {}): string {
  const { sign = false, dash = true, negativeStyle = 'minus' } = options;
  if (value === 0 && dash) return EM_DASH;
  if (value < 0) {
    return negativeStyle === 'parens' ? `($${abs2(value)})` : `${MINUS}$${abs2(value)}`;
  }
  return `${sign ? '+' : ''}$${abs2(value)}`;
}

/** Always "$X.XX" of the magnitude — no sign, no dash. */
export function formatMoneyPlain(value: number): string {
  return `$${abs2(value)}`;
}

/** Compact thousands: "$12.5k" at/above 1,000, otherwise the plain form. */
export function formatMoneyK(value: number): string {
  if (Math.abs(value) >= 1000) {
    return `$${(value / 1000).toLocaleString('en-US', { minimumFractionDigits: 1, maximumFractionDigits: 1 })}k`;
  }
  return formatMoneyPlain(value);
}
