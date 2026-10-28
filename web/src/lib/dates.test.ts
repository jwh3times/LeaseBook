import { afterEach, describe, expect, it } from 'vitest';
import { localDate } from './dates';

const originalTz = process.env.TZ;

afterEach(() => {
  process.env.TZ = originalTz;
});

describe('localDate', () => {
  it('gives the date an evening in US Eastern falls on, not the UTC date', () => {
    process.env.TZ = 'America/New_York';
    // 9 pm Eastern on 12 October.
    expect(localDate('2026-10-13T01:00:00Z')).toBe('2026-10-12');
  });

  it('gives the next date east of Greenwich when UTC is still on the previous one', () => {
    process.env.TZ = 'Asia/Tokyo';
    expect(localDate('2026-10-12T20:00:00Z')).toBe('2026-10-13');
  });

  it('pads single-digit months and days', () => {
    process.env.TZ = 'UTC';
    expect(localDate('2026-03-05T12:00:00Z')).toBe('2026-03-05');
  });

  it('returns the input unchanged when it is not a timestamp', () => {
    expect(localDate('not a date')).toBe('not a date');
  });
});
