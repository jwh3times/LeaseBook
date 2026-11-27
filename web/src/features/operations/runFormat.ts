/**
 * How a run's header reads, shared by run history and the run page (#573) so the two never describe
 * the same run differently.
 */
import type { BulkRunItemSpa } from '@/api';
import type { BadgeTone } from '@/design';

const RUN_TYPE_META: Record<string, { label: string; tone: BadgeTone }> = {
  Rent: { label: 'Rent', tone: 'accent' },
  LateFee: { label: 'Late fee', tone: 'warn' },
  Disbursement: { label: 'Disbursement', tone: 'pos' },
};

export function runTypeMeta(runType: string): { label: string; tone: BadgeTone } {
  return RUN_TYPE_META[runType] ?? { label: runType, tone: 'neutral' };
}

/** The counts and total the run engine wrote into the run's `summary_json`. */
export interface RunSummary {
  posted?: number;
  skipped?: number;
  excluded?: number;
  total?: number;
}

export function parseSummary(json: string | null | undefined): RunSummary {
  try {
    return json ? (JSON.parse(json) as RunSummary) : {};
  } catch {
    return {};
  }
}

export function formatPeriod(year: number | string, month: number | string): string {
  const date = new Date(Number(year), Number(month) - 1, 1);
  return date.toLocaleString('default', { month: 'long', year: 'numeric' });
}

/** A run outcome (CONTEXT.md), as the API contract publishes it. */
export type RunOutcome = BulkRunItemSpa['outcome'];

/**
 * Every run outcome, in the order the run page offers them. Built from a complete record over the
 * contract's union, so an outcome added on the server fails typecheck here rather than going missing
 * from the page's filter.
 */
const OUTCOME_ORDER: Record<RunOutcome, number> = { Posted: 0, Skipped: 1, Excluded: 2 };

export const RUN_OUTCOMES: readonly RunOutcome[] = (
  Object.keys(OUTCOME_ORDER) as RunOutcome[]
).sort((a, b) => OUTCOME_ORDER[a] - OUTCOME_ORDER[b]);
