import { useState, type ReactNode } from 'react';
import type { RunPreviewIssuedCoverageRow } from '@/api';
import { InfoNotice } from '@/components/InfoNotice';
import { useRunPreviewIssuedCoverage, type RunType } from './useRuns';

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

function monthName(year: number, month: number): string {
  return `${MONTHS[month - 1] ?? ''} ${year}`;
}

function sentence(row: RunPreviewIssuedCoverageRow): string {
  const year = Number(row.issuedYear);
  const month = Number(row.issuedMonth);
  const following = month === 12 ? monthName(year + 1, 1) : monthName(year, month + 1);
  const scope = row.propertyAddress ? ` (${row.propertyAddress})` : '';
  return (
    `If confirmed: ${row.ownerName} — the ${row.basis} statement for ${monthName(year, month)}${scope} ` +
    `was already issued; this run's postings will appear as prior-period adjustments on their ` +
    `${following} ${row.basis} statement.`
  );
}

export interface RunPreviewIssuedStatementNoticeProps {
  type: RunType;
  year: number;
  month: number;
  selectedTargetIds: ReadonlySet<string>;
}

/**
 * Before confirmation, reports which issued statements would carry the selected targets forward.
 * The coverage answer belongs to the whole preview and is filtered locally as the selection moves,
 * so ticking a row never re-plans the run or changes its capability freeze.
 */
export function RunPreviewIssuedStatementNotice({
  type,
  year,
  month,
  selectedTargetIds,
}: RunPreviewIssuedStatementNoticeProps) {
  const [dismissed, setDismissed] = useState(false);
  const coverage = useRunPreviewIssuedCoverage(type, year, month);

  return <div role="status">{content()}</div>;

  function notice(children: ReactNode) {
    return (
      <InfoNotice live={false} onDismiss={() => setDismissed(true)}>
        {children}
      </InfoNotice>
    );
  }

  function content() {
    if (dismissed || selectedTargetIds.size === 0 || coverage.isPending) return null;

    if (coverage.isError) {
      return notice(<span>Couldn&apos;t check for issued statements affected by this run.</span>);
    }

    const selected = coverage.data.rows.filter((row) => selectedTargetIds.has(row.targetId));
    const unique = new Map<string, RunPreviewIssuedCoverageRow>();
    for (const row of selected) {
      unique.set(`${row.ownerId}:${row.basis}:${row.propertyId ?? 'owner'}`, row);
    }
    const rows = Array.from(unique.values());
    if (rows.length === 0) return null;

    if (rows.length === 1) {
      return notice(<span>{sentence(rows[0]!)}</span>);
    }

    const owners = new Set(rows.map((row) => row.ownerId)).size;
    return notice(
      <>
        <span>
          If confirmed: statements already issued for {owners} {owners === 1 ? 'owner' : 'owners'};
          this run&apos;s postings will appear as prior-period adjustments on the statement for the
          month after each.
        </span>
        <details>
          <summary>Show affected statements</summary>
          <ul className="pf-info-notice-list">
            {rows.map((row) => (
              <li key={`${row.ownerId}:${row.basis}:${row.propertyId ?? 'owner'}`}>
                {sentence(row)}
              </li>
            ))}
          </ul>
        </details>
      </>,
    );
  }
}
