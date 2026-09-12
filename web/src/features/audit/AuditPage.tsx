import { useState } from 'react';
import { asApiError, type ApiError, type AuditLogRow } from '@/api';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { ErrorAction } from '@/components/ErrorAction';
import { QueryErrorState } from '@/components/QueryErrorState';
import {
  Button,
  Card,
  CardHeader,
  EmptyState,
  Input,
  Select,
  Table,
  type TableColumn,
} from '@/design';
import { useSession } from '@/features/auth/useSession';
import { AuditEventDrawer } from './AuditEventDrawer';
import {
  DEFAULT_PAGE_SIZE,
  EMPTY_QUERY,
  SYSTEM_ACTOR,
  exportAuditCsv,
  useAuditEvents,
  useAuditFilterOptions,
  type AuditQuery,
} from './audit';
import { formatWhen } from './format';
import './audit.css';

/**
 * The PMAdmin audit-review page (#321) — every recorded change, narrowed by period, actor, record
 * type and event kind, with one event's payload behind a row click.
 * <p>
 * Wider than the compliance pack's audit extract on purpose: the extract answers an examiner's
 * question about a reconciled period's money, and this answers a manager's question about anything
 * that happened. The filter selects are fed by an auxiliary read, so when that read fails they show
 * the failure rather than an empty vocabulary that would read as "this org has no record types".
 */
export function AuditPage() {
  const [query, setQuery] = useState<AuditQuery>(EMPTY_QUERY);
  const [openEventId, setOpenEventId] = useState<string | null>(null);
  const [exportError, setExportError] = useState<ApiError | null>(null);
  const [exporting, setExporting] = useState(false);

  const session = useSession();
  // Only a *confirmed* non-admin is turned away. A failed session read is not evidence of a role, so
  // it falls through to the reads, which carry their own error states — and the endpoint refuses a
  // non-admin regardless of what this decided. A pending one waits: asking for a trail before
  // knowing who is asking spends a request the answer may discard.
  const barred = session.isSuccess && session.data?.role !== 'PMAdmin';
  const mayRead = !session.isPending && !barred;

  const options = useAuditFilterOptions(mayRead);
  const events = useAuditEvents(query, mayRead);

  // Any filter change returns to page 1: page 3 of the old result set is not page 3 of the new one.
  const narrow = (patch: Partial<AuditQuery>) =>
    setQuery((current) => ({ ...current, ...patch, page: 1 }));

  async function runExport() {
    setExporting(true);
    setExportError(null);
    try {
      await exportAuditCsv(query);
    } catch (error) {
      setExportError(asApiError(error));
    } finally {
      setExporting(false);
    }
  }

  const columns: TableColumn<AuditLogRow>[] = [
    { key: 'when', header: 'When (UTC)', render: (row) => formatWhen(row.occurredAt) },
    { key: 'entityType', header: 'Record type', render: (row) => row.entityType },
    { key: 'action', header: 'Event', render: (row) => row.action },
    {
      key: 'actor',
      header: 'Actor',
      render: (row) => (
        <div className="col gap4">
          <span>{row.actorName}</span>
          {row.actorEmail && <span className="t3 fs12">{row.actorEmail}</span>}
        </div>
      ),
    },
    {
      // A row click opens the drawer, but a click is not a keyboard path: the Table primitive puts
      // its handler on the <tr>, which nothing can focus. This is the focusable control that opens
      // the same drawer, labelled per row so a screen reader hears which event it opens rather than
      // fifty identical "View"s.
      key: 'open',
      header: '',
      render: (row) => (
        <Button
          variant="ghost"
          size="sm"
          aria-label={`View ${row.entityType} ${row.action} at ${formatWhen(row.occurredAt)}`}
          onClick={() => setOpenEventId(row.id)}
        >
          View
        </Button>
      ),
    },
  ];

  // The generated contract widens an int32 to `number | string`; coerce once, here, rather than at
  // each use. Only read when the query succeeded — a pending or failed read has no total.
  const total = events.isSuccess ? Number(events.data.total) : 0;
  const lastPage = Math.max(1, Math.ceil(total / DEFAULT_PAGE_SIZE));

  if (barred) {
    return (
      <div className="pf-fade">
        <div className="pf-pagehd">
          <div>
            <h2>Audit log</h2>
          </div>
        </div>
        <Card>
          <EmptyState
            icon="alert"
            title="Admin access required"
            description="The audit log is available to property-manager administrators."
          />
        </Card>
      </div>
    );
  }

  return (
    <div className="pf-fade">
      <div className="pf-pagehd">
        <div>
          <h2>Audit log</h2>
          <p>Every recorded change, and who made it.</p>
        </div>
        {/* Blocked until the list read succeeds: an export of rows nobody could load is not an export. */}
        <Button
          variant="ghost"
          size="sm"
          icon="download"
          onClick={() => void runExport()}
          disabled={!events.isSuccess || exporting}
        >
          {exporting ? 'Exporting…' : 'Export CSV'}
        </Button>
      </div>

      {exportError && (
        <ApiErrorNotice
          error={exportError}
          fallback="Failed to export the audit log."
          kind="read"
          style={{ marginBottom: 'var(--gap)' }}
        />
      )}

      <Card className="pf-audit-filter-card">
        <CardHeader title="Filters" />
        {options.isError ? (
          <div className="col gap6">
            <ApiErrorNotice
              error={options.error}
              fallback="Failed to load the audit filters."
              kind="read"
            />
            <ErrorAction
              error={options.error}
              onRetry={() => void options.refetch()}
              retrying={options.isFetching}
            />
          </div>
        ) : (
          <div className="pf-audit-filters" role="group" aria-label="Audit filters">
            <label className="col gap4">
              <span className="t3 fs12">From</span>
              <Input
                type="date"
                value={query.from ?? ''}
                onChange={(e) => narrow({ from: e.target.value || undefined })}
              />
            </label>
            <label className="col gap4">
              <span className="t3 fs12">To</span>
              <Input
                type="date"
                value={query.to ?? ''}
                onChange={(e) => narrow({ to: e.target.value || undefined })}
              />
            </label>
            <label className="col gap4">
              <span className="t3 fs12">Actor</span>
              <Select
                value={query.actor ?? ''}
                disabled={options.isPending}
                onChange={(e) => narrow({ actor: e.target.value || undefined })}
              >
                <option value="">Anyone</option>
                <option value={SYSTEM_ACTOR}>System (automated)</option>
                {options.data?.actors.map((actor) => (
                  <option key={actor.id} value={actor.id}>
                    {actor.name}
                  </option>
                ))}
              </Select>
            </label>
            <label className="col gap4">
              <span className="t3 fs12">Record type</span>
              <Select
                value={query.entityType ?? ''}
                disabled={options.isPending}
                onChange={(e) => narrow({ entityType: e.target.value || undefined })}
              >
                <option value="">All records</option>
                {options.data?.entityTypes.map((entityType) => (
                  <option key={entityType} value={entityType}>
                    {entityType}
                  </option>
                ))}
              </Select>
            </label>
            <label className="col gap4">
              <span className="t3 fs12">Event</span>
              <Select
                value={query.action ?? ''}
                disabled={options.isPending}
                onChange={(e) => narrow({ action: e.target.value || undefined })}
              >
                <option value="">All events</option>
                {options.data?.actions.map((action) => (
                  <option key={action} value={action}>
                    {action}
                  </option>
                ))}
              </Select>
            </label>
            <Button variant="ghost" size="sm" onClick={() => setQuery(EMPTY_QUERY)}>
              Clear
            </Button>
            {/* The server bounds the period in UTC; saying so is cheaper than a screen whose column
                and whose filter quietly disagree for anyone east or west of it. */}
            <span className="t3 fs12">Dates and times are UTC.</span>
          </div>
        )}
      </Card>

      <Card>
        <CardHeader title="Events" sub={events.isSuccess ? `${total} total` : undefined} />
        {events.isPending ? (
          <div className="col gap8">
            {[0, 1, 2, 3, 4].map((row) => (
              <div key={row} className="pf-skeleton" style={{ height: 20 }} />
            ))}
          </div>
        ) : events.isError ? (
          <QueryErrorState
            query={events}
            title="Couldn't load the audit log"
            fallback="Failed to load the audit log."
          />
        ) : events.data.rows.length === 0 ? (
          <EmptyState
            icon="doc"
            title="No matching events"
            description="No recorded change matches these filters."
          />
        ) : (
          <>
            <Table
              columns={columns}
              rows={events.data.rows}
              rowKey={(row) => row.id}
              onRowClick={(row) => setOpenEventId(row.id)}
            />
            <div className="pf-audit-pager">
              <Button
                variant="ghost"
                size="sm"
                disabled={query.page <= 1}
                onClick={() => setQuery((c) => ({ ...c, page: c.page - 1 }))}
              >
                Previous
              </Button>
              <span className="t3 fs12">
                Page {query.page} of {lastPage}
              </span>
              <Button
                variant="ghost"
                size="sm"
                disabled={query.page >= lastPage}
                onClick={() => setQuery((c) => ({ ...c, page: c.page + 1 }))}
              >
                Next
              </Button>
            </div>
          </>
        )}
      </Card>

      {openEventId && (
        <AuditEventDrawer eventId={openEventId} onClose={() => setOpenEventId(null)} />
      )}
    </div>
  );
}
