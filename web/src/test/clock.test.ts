import { describe, expect, it } from 'vitest';

// The suite runs in a US time zone at an evening instant, when the local date is already behind the
// UTC date (#643). CI's machines run in UTC, where slicing an ISO string gives the right date, so a
// date bug that only shows in US evenings would otherwise pass everywhere the suite runs.
describe('the unit suite clock', () => {
  it('runs in America/New_York whatever zone the machine has', () => {
    expect(Intl.DateTimeFormat().resolvedOptions().timeZone).toBe('America/New_York');
  });

  it('starts each test in the evening, when the local date is a day behind the UTC date', () => {
    const now = new Date();
    const local = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;

    expect(now.getHours()).toBe(22);
    expect(now.toISOString().slice(0, 10)).not.toBe(local);
  });

  it('keeps time moving, so code that measures elapsed time still sees it pass', async () => {
    const start = Date.now();
    await new Promise((resolve) => setTimeout(resolve, 30));

    expect(Date.now()).toBeGreaterThan(start);
  });
});
