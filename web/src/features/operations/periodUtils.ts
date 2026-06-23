/**
 * Shared period utilities for the operations run screens.
 */

/** Returns the current calendar year and month (1-based). */
export function currentPeriod(): { year: number; month: number } {
  const now = new Date();
  return { year: now.getFullYear(), month: now.getMonth() + 1 };
}
