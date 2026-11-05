import { describe, expect, it } from 'vitest';
import { dateInZone, ORG_TIME_ZONES } from '@/lib/orgCalendar';

// Every expected date is worked out by hand from the zone's offset on that day (#642).
describe('dateInZone', () => {
  it.each([
    // 21:00 EDT on 31 Oct 2026 is 01:00 UTC on 1 Nov.
    ['2026-11-01T01:00:00Z', 'America/New_York', '2026-10-31'],
    // 23:30 PDT on 31 Oct 2026 is 06:30 UTC on 1 Nov; New York is already on 1 Nov.
    ['2026-11-01T06:30:00Z', 'America/Los_Angeles', '2026-10-31'],
    ['2026-11-01T06:30:00Z', 'America/New_York', '2026-11-01'],
    // Phoenix keeps UTC-7 all year; Honolulu is UTC-10.
    ['2026-07-01T06:59:00Z', 'America/Phoenix', '2026-06-30'],
    ['2027-01-01T09:59:00Z', 'Pacific/Honolulu', '2026-12-31'],
    // A morning instant is the same date everywhere.
    ['2026-10-31T15:00:00Z', 'America/Anchorage', '2026-10-31'],
  ])('at %s in %s it is %s', (instant, zone, expected) => {
    expect(dateInZone(zone, new Date(instant))).toBe(expected);
  });

  it('is the organization’s date, not the UTC date, on a US evening', () => {
    // The suite runs at 22:00 in New York, when the UTC date is already tomorrow's.
    const now = new Date();
    expect(dateInZone('America/New_York', now)).not.toBe(now.toISOString().slice(0, 10));
  });

  it('resolves every zone the Settings select offers', () => {
    for (const zone of ORG_TIME_ZONES) {
      expect(dateInZone(zone.id, new Date('2026-10-31T15:00:00Z'))).toBe('2026-10-31');
    }
  });
});
