import { describe, expect, it } from 'vitest';
import { formatMoney, formatMoneyK, formatMoneyPlain } from './formatMoney';

describe('formatMoney', () => {
  it('renders an em-dash for zero by default', () => {
    expect(formatMoney(0)).toBe('—');
  });

  it('can disable the zero dash', () => {
    expect(formatMoney(0, { dash: false })).toBe('$0.00');
  });

  it('uses a Unicode minus sign for negatives', () => {
    expect(formatMoney(-420)).toBe('−$420.00');
  });

  it('supports accounting parentheses for negatives', () => {
    expect(formatMoney(-8200, { negativeStyle: 'parens' })).toBe('($8,200.00)');
  });

  it('adds a leading plus when requested', () => {
    expect(formatMoney(1450, { sign: true })).toBe('+$1,450.00');
  });

  it('groups thousands and keeps two decimals', () => {
    expect(formatMoney(248930.14)).toBe('$248,930.14');
  });
});

describe('formatMoneyPlain', () => {
  it('renders magnitude only', () => {
    expect(formatMoneyPlain(-75)).toBe('$75.00');
  });
});

describe('formatMoneyK', () => {
  it('compacts values at or above a thousand', () => {
    expect(formatMoneyK(483620.69)).toBe('$483.6k');
  });

  it('keeps small values in the plain form', () => {
    expect(formatMoneyK(420)).toBe('$420.00');
  });
});
