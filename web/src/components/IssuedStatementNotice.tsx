import type { CSSProperties, ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { getApiStatementsIssuedCoverage, unwrap, type IssuedStatementCoverageRow } from '@/api';
import { InfoNotice } from './InfoNotice';

/** What was just posted: specific ledger entries, or every posting of a confirmed run. */
export type IssuedCoverageTarget = { entryIds: string[] } | { runId: string };

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

/**
 * One (owner, basis, scope) line: the latest issued statement — the document now out of date — and the
 * statement that will itemize the change. ADR-045 carries forward only from the immediately preceding
 * month, so that is the statement for the month after the issued one, named exactly rather than
 * promised as "their next statement" (which a skipped month would make untrue).
 */
export function issuedStatementSentence(
  row: IssuedStatementCoverageRow,
  subject: 'This entry' | 'These postings',
): string {
  const year = Number(row.issuedYear);
  const month = Number(row.issuedMonth);
  const following = month === 12 ? monthName(year + 1, 1) : monthName(year, month + 1);
  const scope = row.propertyAddress ? ` (${row.propertyAddress})` : '';
  const adjustment =
    subject === 'This entry' ? 'a prior-period adjustment' : 'prior-period adjustments';
  return (
    `${row.ownerName} — the ${row.basis} statement for ${monthName(year, month)}${scope} was already issued. ` +
    `${subject} will appear as ${adjustment} on their ${following} ${row.basis} statement.`
  );
}

export const issuedCoverageKey = (target: IssuedCoverageTarget) =>
  ['statements', 'issued-coverage', target] as const;

export interface IssuedStatementNoticeProps {
  target: IssuedCoverageTarget;
  onDismiss?: () => void;
  style?: CSSProperties;
}

/**
 * The #377 heads-up: after a successful post, which already-issued owner statements it will be carried
 * forward into. Informational only — it is read after posting, stores nothing, and never blocks.
 * <p>
 * The `role="status"` container is mounted immediately and stays empty while checking or when nothing
 * is affected, so the text is announced when it arrives. A failed check says so rather than staying
 * silent, because silence would read as "no issued statement was affected".
 */
export function IssuedStatementNotice({ target, onDismiss, style }: IssuedStatementNoticeProps) {
  const isRun = 'runId' in target;
  const coverage = useQuery({
    queryKey: issuedCoverageKey(target),
    queryFn: () =>
      unwrap(
        getApiStatementsIssuedCoverage({
          query: isRun ? { runId: target.runId } : { entryIds: target.entryIds },
        }),
        'Failed to check for issued statements',
      ),
    // The posting already succeeded; this is a heads-up, not a read worth retrying or refreshing.
    retry: false,
    staleTime: Infinity,
  });

  return <div role="status">{content()}</div>;

  function notice(children: ReactNode) {
    return (
      <InfoNotice live={false} onDismiss={onDismiss} style={style}>
        {children}
      </InfoNotice>
    );
  }

  function content() {
    if (coverage.isPending) return null;

    if (coverage.isError) {
      return notice(
        <span>
          Couldn&apos;t check for issued statements affected by{' '}
          {isRun ? 'these postings' : 'this entry'}.
        </span>,
      );
    }

    const rows = coverage.data.rows;
    if (rows.length === 0) return null;

    const key = (row: IssuedStatementCoverageRow) =>
      `${row.ownerId}:${row.basis}:${row.propertyId ?? 'owner'}`;

    if (!isRun) {
      return notice(
        rows.map((row) => <span key={key(row)}>{issuedStatementSentence(row, 'This entry')}</span>),
      );
    }

    if (rows.length === 1) {
      return notice(<span>{issuedStatementSentence(rows[0]!, 'These postings')}</span>);
    }

    const owners = new Set(rows.map((row) => row.ownerId)).size;
    return notice(
      <>
        <span>
          Statements already issued for {owners} {owners === 1 ? 'owner' : 'owners'}; these postings
          will appear as prior-period adjustments on the statement for the month after each.
        </span>
        <details>
          <summary>Show affected statements</summary>
          <ul className="pf-info-notice-list">
            {rows.map((row) => (
              <li key={key(row)}>{issuedStatementSentence(row, 'These postings')}</li>
            ))}
          </ul>
        </details>
      </>,
    );
  }
}
