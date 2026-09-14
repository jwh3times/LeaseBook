import { useState, type ReactNode } from 'react';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { ErrorAction } from '@/components/ErrorAction';
import { InfoNotice } from '@/components/InfoNotice';
import { IssuedStatementCoverageNotice } from '@/components/IssuedStatementCoverageNotice';
import { useRunPreviewIssuedCoverage, type RunType } from './useRuns';

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
    if (dismissed || selectedTargetIds.size === 0) return null;

    if (coverage.isPending) {
      return (
        <div
          className="pf-skeleton"
          aria-label="Checking for issued statements affected by this run"
          style={{ height: 20 }}
        />
      );
    }

    if (coverage.isError) {
      return (
        <div className="col gap6">
          {notice(<span>Couldn&apos;t check for issued statements affected by this run.</span>)}
          <ApiErrorNotice
            error={coverage.error}
            fallback="Failed to check for issued statements affected by this run."
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
        rows={coverage.data.rows.filter((row) => selectedTargetIds.has(row.targetId))}
        mode="preview-run"
        onDismiss={() => setDismissed(true)}
      />
    );
  }
}
