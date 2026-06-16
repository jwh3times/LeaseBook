import { EmptyState, Icon } from '@/design';
import { Modal } from '@/lib/Modal';
import { useEntryAudit } from './ledger';

interface AuditDrawerProps {
  entryId: string;
  onClose: () => void;
}

const ACTION_LABEL: Record<string, string> = {
  insert: 'Posted',
  update: 'Updated',
  delete: 'Deleted',
};

function formatWhen(iso: string): string {
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString();
}

/**
 * The per-entry audit trail (§C.4 / P56): who/when/what for the entry and its reversal, newest first.
 * Opened from a row's History action; the actor name comes from the org-filtered identity lookup.
 */
export function AuditDrawer({ entryId, onClose }: AuditDrawerProps) {
  const audit = useEntryAudit(entryId, true);

  return (
    <Modal title="History" onClose={onClose}>
      <div className="pf-modal-body">
        {audit.isPending ? (
          <div className="col gap8">
            {[0, 1].map((row) => (
              <div key={row} className="pf-skeleton" style={{ height: 20 }} />
            ))}
          </div>
        ) : audit.isError ? (
          <EmptyState
            icon="alert"
            title="Couldn't load the history"
            description="Please retry in a moment."
          />
        ) : audit.data.rows.length === 0 ? (
          <EmptyState
            icon="doc"
            title="No history yet"
            description="Activity on this entry will appear here."
          />
        ) : (
          <ul className="pf-audit">
            {audit.data.rows.map((row, index) => (
              <li key={index} className="pf-audit-row">
                <span className="pf-audit-dot">
                  <Icon name="check" size={13} />
                </span>
                <div className="col gap2">
                  <div className="row gap6">
                    <strong>{row.actorName}</strong>
                    <span className="t3">{ACTION_LABEL[row.action] ?? row.action}</span>
                  </div>
                  {row.actorEmail && <span className="t3 fs12">{row.actorEmail}</span>}
                  <span className="t3 fs12">{formatWhen(row.occurredAt)}</span>
                </div>
              </li>
            ))}
          </ul>
        )}
      </div>
    </Modal>
  );
}
