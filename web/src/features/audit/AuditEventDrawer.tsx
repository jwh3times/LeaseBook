import { Badge, EmptyState, Table, type TableColumn } from '@/design';
import { Modal } from '@/components/Modal';
import { QueryErrorState } from '@/components/QueryErrorState';
import type { AuditFieldChange } from '@/api';
import { useAuditEvent } from './audit';
import { formatWhen } from './format';

interface AuditEventDrawerProps {
  eventId: string;
  onClose: () => void;
}

/** A withheld value renders as its marker, not as blank — see AuditFieldRedaction. */
function Value({ value }: { value: string | null }) {
  return value === null ? (
    <span className="t3">—</span>
  ) : (
    <span className="pf-audit-value">{value}</span>
  );
}

/**
 * One audit event's payload, field by field (#321). An update lists only the columns that moved; an
 * insert has no before side and a delete no after side.
 * <p>
 * A withheld field is labelled in words as well as tone, because the reviewer has to be able to tell
 * "you may not read this" from "this was cleared" — and because status is never colour alone.
 */
export function AuditEventDrawer({ eventId, onClose }: AuditEventDrawerProps) {
  const event = useAuditEvent(eventId);

  const columns: TableColumn<AuditFieldChange>[] = [
    {
      key: 'field',
      header: 'Field',
      render: (change) => (
        <div className="row gap6">
          <span>{change.field}</span>
          {change.redacted && (
            <Badge tone="warn" dot>
              Withheld
            </Badge>
          )}
        </div>
      ),
    },
    { key: 'before', header: 'Before', render: (change) => <Value value={change.before} /> },
    { key: 'after', header: 'After', render: (change) => <Value value={change.after} /> },
  ];

  return (
    <Modal title="Audit event" onClose={onClose}>
      <div className="pf-modal-body">
        {event.isPending ? (
          <div className="col gap8">
            {[0, 1, 2].map((row) => (
              <div key={row} className="pf-skeleton" style={{ height: 20 }} />
            ))}
          </div>
        ) : event.isError ? (
          <QueryErrorState
            query={event}
            title="Couldn't load the event"
            fallback="Failed to load the audit event."
          />
        ) : (
          <div className="col gap12">
            <dl className="pf-audit-meta">
              <div>
                <dt>When</dt>
                <dd>{formatWhen(event.data.event.occurredAt)}</dd>
              </div>
              <div>
                <dt>Actor</dt>
                <dd>
                  {event.data.event.actorName}
                  {event.data.event.actorEmail && (
                    <span className="t3 fs12"> · {event.data.event.actorEmail}</span>
                  )}
                </dd>
              </div>
              <div>
                <dt>Record</dt>
                <dd>
                  {event.data.event.entityType} · {event.data.event.action}
                </dd>
              </div>
              <div>
                <dt>Record id</dt>
                <dd className="pf-num fs12">{event.data.event.entityId}</dd>
              </div>
            </dl>

            {event.data.changes.length === 0 ? (
              <EmptyState
                icon="doc"
                title="No recorded values"
                description="This event records that it happened, and carries no field values."
              />
            ) : (
              <Table
                columns={columns}
                rows={event.data.changes}
                rowKey={(change) => change.field}
              />
            )}
          </div>
        )}
      </div>
    </Modal>
  );
}
