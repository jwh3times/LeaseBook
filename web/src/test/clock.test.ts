import { describe, expect, it } from 'vitest';

// Guards the suite's zone (`test.env` in vite.config.ts) and evening clock (setup.ts), #643.
describe('the unit suite clock', () => {
  it('runs in America/New_York whatever zone the machine has', () => {
    // The variable as well as the resolved zone: on a machine already in New York, the zone alone
    // would stay right with the setting gone.
    expect(process.env.TZ).toBe('America/New_York');
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
