import type { CSSProperties } from 'react';
import type { IssuedStatementCoverageRow } from '@/api';
import { InfoNotice } from './InfoNotice';

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

export type IssuedStatementCoverageMode = 'entry' | 'posted-run' | 'preview-run';
export type IssuedStatementSubject = 'This entry' | 'These postings' | 'If confirmed';

function monthName(year: number, month: number): string {
  return `${MONTHS[month - 1] ?? ''} ${year}`;
}

/**
 * One (owner, basis, scope) line: the latest issued statement and the statement that will itemize
 * the change. ADR-045 carries forward only from the immediately preceding month, so the destination
 * month is named exactly rather than promised as "their next statement".
 */
function issuedStatementSentence(
  row: IssuedStatementCoverageRow,
  subject: IssuedStatementSubject,
): string {
  const year = Number(row.issuedYear);
  const month = Number(row.issuedMonth);
  const following = month === 12 ? monthName(year + 1, 1) : monthName(year, month + 1);
  const scope = row.propertyAddress ? ` (${row.propertyAddress})` : '';

  if (subject === 'If confirmed') {
    return (
      `If confirmed: ${row.ownerName} — the ${row.basis} statement for ${monthName(year, month)}${scope} ` +
      `was already issued; this run's postings will appear as prior-period adjustments on their ` +
      `${following} ${row.basis} statement.`
    );
  }

  const adjustment =
    subject === 'This entry' ? 'a prior-period adjustment' : 'prior-period adjustments';
  return (
    `${row.ownerName} — the ${row.basis} statement for ${monthName(year, month)}${scope} was already issued. ` +
    `${subject} will appear as ${adjustment} on their ${following} ${row.basis} statement.`
  );
}

export interface IssuedStatementCoverageNoticeProps {
  rows: readonly IssuedStatementCoverageRow[];
  mode: IssuedStatementCoverageMode;
  onDismiss?: () => void;
  style?: CSSProperties;
}

/** Shared successful-state presentation for the after-post (#377) and preview (#380) notices. */
export function IssuedStatementCoverageNotice({
  rows,
  mode,
  onDismiss,
  style,
}: IssuedStatementCoverageNoticeProps) {
  const unique = new Map<string, IssuedStatementCoverageRow>();
  for (const row of rows) {
    unique.set(`${row.ownerId}:${row.basis}:${row.propertyId ?? 'owner'}`, row);
  }
  const statements = Array.from(unique.values());
  if (statements.length === 0) return null;

  const subject =
    mode === 'entry' ? 'This entry' : mode === 'posted-run' ? 'These postings' : 'If confirmed';

  if (mode === 'entry') {
    return (
      <InfoNotice live={false} onDismiss={onDismiss} style={style}>
        {statements.map((row) => (
          <span key={rowKey(row)}>{issuedStatementSentence(row, subject)}</span>
        ))}
      </InfoNotice>
    );
  }

  if (statements.length === 1) {
    return (
      <InfoNotice live={false} onDismiss={onDismiss} style={style}>
        <span>{issuedStatementSentence(statements[0]!, subject)}</span>
      </InfoNotice>
    );
  }

  const owners = new Set(statements.map((row) => row.ownerId)).size;
  return (
    <InfoNotice live={false} onDismiss={onDismiss} style={style}>
      <span>
        {mode === 'posted-run' ? (
          <>
            Statements already issued for {owners} {owners === 1 ? 'owner' : 'owners'}; these
            postings will appear as prior-period adjustments on the statement for the month after
            each.
          </>
        ) : (
          <>
            If confirmed: statements already issued for {owners} {owners === 1 ? 'owner' : 'owners'}
            ; this run&apos;s postings will appear as prior-period adjustments on the statement for
            the month after each.
          </>
        )}
      </span>
      <details>
        <summary>Show affected statements</summary>
        <ul className="pf-info-notice-list">
          {statements.map((row) => (
            <li key={rowKey(row)}>{issuedStatementSentence(row, subject)}</li>
          ))}
        </ul>
      </details>
    </InfoNotice>
  );
}

function rowKey(row: IssuedStatementCoverageRow): string {
  return `${row.ownerId}:${row.basis}:${row.propertyId ?? 'owner'}`;
}
