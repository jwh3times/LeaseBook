import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import {
  download,
  getApiAuditEvents,
  getApiAuditEventsById,
  getApiAuditEventsCsv,
  getApiAuditFilters,
  unwrap,
  type AuditEventDetail,
  type AuditFilterOptions,
  type AuditLogResponse,
} from '@/api';

/** The system actor: every automated write, whatever process made it (ADR-039). */
export const SYSTEM_ACTOR = 'system';

export const DEFAULT_PAGE_SIZE = 50;

/**
 * What the reviewer has narrowed to. `actor` is a user id or {@link SYSTEM_ACTOR} — one parameter,
 * because "this person" and "the system" are answers to the same question and a surface that let both
 * be set at once would be asking for rows that cannot exist.
 */
export interface AuditQuery {
  from?: string;
  to?: string;
  actor?: string;
  entityType?: string;
  action?: string;
  page: number;
}

export const EMPTY_QUERY: AuditQuery = { page: 1 };

export const auditFilterOptionsKey = ['audit-filter-options'] as const;
export const auditEventsKey = (query: AuditQuery) => ['audit-events', query] as const;
export const auditEventKey = (id: string) => ['audit-event', id] as const;

/** The query string for a request — blank filters are omitted rather than sent as "". */
function params(query: AuditQuery) {
  return {
    ...(query.from ? { from: query.from } : {}),
    ...(query.to ? { to: query.to } : {}),
    ...(query.actor ? { actor: query.actor } : {}),
    ...(query.entityType ? { entityType: query.entityType } : {}),
    ...(query.action ? { action: query.action } : {}),
  };
}

/**
 * Entity types, actions, and this org's people — the vocabularies the selects are built from.
 * `enabled` is how the page avoids asking for a trail it already knows the caller cannot read; the
 * endpoint is what refuses them.
 */
export function useAuditFilterOptions(enabled = true): UseQueryResult<AuditFilterOptions> {
  return useQuery({
    queryKey: auditFilterOptionsKey,
    queryFn: () => unwrap(getApiAuditFilters(), 'Failed to load the audit filters'),
    staleTime: 5 * 60_000,
    enabled,
  });
}

export function useAuditEvents(
  query: AuditQuery,
  enabled = true,
): UseQueryResult<AuditLogResponse> {
  return useQuery({
    queryKey: auditEventsKey(query),
    enabled,
    queryFn: () =>
      unwrap(
        getApiAuditEvents({
          query: { ...params(query), page: query.page, pageSize: DEFAULT_PAGE_SIZE },
        }),
        'Failed to load the audit log',
      ),
  });
}

/**
 * One event's payload diff. Fetched only when the drawer is open: browsing a trail should not ship
 * every snapshot behind it, and the payloads are the part of an audit row worth not moving around.
 */
export function useAuditEvent(id: string | null): UseQueryResult<AuditEventDetail> {
  return useQuery({
    queryKey: auditEventKey(id ?? ''),
    queryFn: () =>
      unwrap(getApiAuditEventsById({ path: { id: id! } }), 'Failed to load the audit event'),
    enabled: id !== null,
  });
}

/** The filtered rows as a CSV download — the same narrowing that is on screen. */
export function exportAuditCsv(query: AuditQuery): Promise<void> {
  const stamp = new Date().toISOString().slice(0, 10);
  return download(
    () => getApiAuditEventsCsv({ query: params(query), parseAs: 'blob' }),
    `audit-log-${stamp}.csv`,
    'Failed to export the audit log.',
  );
}
