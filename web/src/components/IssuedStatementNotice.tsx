import type { CSSProperties, ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  getApiStatementsIssuedCoverage,
  unwrap,
  type ApiError,
  type IssuedStatementCoverageResponse,
} from '@/api';
import { ApiErrorNotice } from './ApiErrorNotice';
import { ErrorAction } from './ErrorAction';
import { InfoNotice } from './InfoNotice';
import { IssuedStatementCoverageNotice } from './IssuedStatementCoverageNotice';

/** What was just posted: specific ledger entries, or every posting of a confirmed run. */
export type IssuedCoverageTarget = { entryIds: string[] } | { runId: string };

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
  const coverage = useQuery<IssuedStatementCoverageResponse, ApiError>({
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
    if (coverage.isPending) {
      return (
        <div
          className="pf-skeleton"
          aria-label={`Checking for issued statements affected by ${isRun ? 'these postings' : 'this entry'}`}
          style={{ height: 20 }}
        />
      );
    }

    if (coverage.isError) {
      return (
        <div className="col gap6">
          {notice(
            <span>
              Couldn&apos;t check for issued statements affected by{' '}
              {isRun ? 'these postings' : 'this entry'}.
            </span>,
          )}
          <ApiErrorNotice
            error={coverage.error}
            fallback="Failed to check for issued statements."
            kind="read"
          />
          <ErrorAction
            error={coverage.error}
            onRetry={() => void coverage.refetch()}
            retrying={coverage.isFetching}
          />
        </div>
      );
    }

    return (
      <IssuedStatementCoverageNotice
        rows={coverage.data.rows}
        mode={isRun ? 'posted-run' : 'entry'}
        onDismiss={onDismiss}
        style={style}
      />
    );
  }
}
