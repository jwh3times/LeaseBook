import type { UseQueryResult } from '@tanstack/react-query';
import { useEffect, useMemo, useState, type KeyboardEvent } from 'react';
import {
  Button,
  Card,
  EmptyState,
  type IconName,
  SearchBox,
  Table,
  type TableColumn,
} from '@/design';
import { useSetRecordOrder, type EntityKind } from './recordNav';
import { trackInteraction } from '@/lib/telemetry';

interface IndexViewProps<T> {
  kind: EntityKind;
  title: string;
  count?: number | string;
  query: UseQueryResult<{ items: T[] } | undefined>;
  columns: TableColumn<T>[];
  rowKey: (row: T) => string;
  /** Client-side instant filter (P42): does this row match the typed query? */
  matches: (row: T, q: string) => boolean;
  onOpen: (row: T) => void;
  onNew: () => void;
  newLabel: string;
  searchPlaceholder: string;
  emptyTitle: string;
  emptyIcon?: IconName;
}

/**
 * Shared index scaffold (§C.3/§C.7): instant client-side filter over the loaded page, keyboard row
 * navigation (↑/↓ select, Enter opens), row-click to detail, and the required empty/loading/error
 * states. Publishes the current filtered order to the record-nav store for the quick-switcher.
 */
export function IndexView<T>({
  kind,
  title,
  count,
  query,
  columns,
  rowKey,
  matches,
  onOpen,
  onNew,
  newLabel,
  searchPlaceholder,
  emptyTitle,
  emptyIcon = 'doc',
}: IndexViewProps<T>) {
  const [q, setQ] = useState('');
  const [selected, setSelected] = useState(0);
  const setOrder = useSetRecordOrder(kind);

  const items = useMemo(() => query.data?.items ?? [], [query.data]);
  const filtered = useMemo(() => {
    const needle = q.trim().toLowerCase();
    return needle ? items.filter((row) => matches(row, needle)) : items;
  }, [items, q, matches]);

  useEffect(() => setSelected(0), [q, items]);
  useEffect(() => setOrder(filtered.map(rowKey)), [filtered, rowKey, setOrder]);

  // Reaching any entity from a list row is within the ≤ 2-interaction budget (§C.8 / P47).
  function open(row: T) {
    trackInteraction('entity-jump', 2, true);
    onOpen(row);
  }

  function onSearchKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      setSelected((index) => Math.min(index + 1, filtered.length - 1));
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      setSelected((index) => Math.max(index - 1, 0));
    } else if (event.key === 'Enter') {
      const row = filtered[selected];
      if (row) {
        event.preventDefault();
        open(row);
      }
    }
  }

  return (
    <div className="pf-fade">
      <div className="pf-pagehd">
        <div>
          <h2>
            {title}
            {count !== undefined && <span className="pf-count"> · {count}</span>}
          </h2>
        </div>
        <Button variant="primary" size="sm" icon="plus" onClick={onNew}>
          {newLabel}
        </Button>
      </div>

      <div className="row gap12 mb12">
        <SearchBox
          value={q}
          placeholder={searchPlaceholder}
          onChange={(event) => setQ(event.target.value)}
          onKeyDown={onSearchKeyDown}
          aria-label={searchPlaceholder}
        />
      </div>

      <Card>
        {query.isPending ? (
          <ListSkeleton columns={columns.length} />
        ) : query.isError ? (
          <div className="pf-pad">
            <EmptyState
              icon="alert"
              title="Couldn’t load this list"
              description="Try again in a moment."
            />
          </div>
        ) : filtered.length === 0 ? (
          <div className="pf-pad">
            <EmptyState
              icon={emptyIcon}
              title={items.length === 0 ? emptyTitle : 'No matches'}
              description={items.length === 0 ? undefined : `Nothing matches “${q}”.`}
              action={
                items.length === 0 ? (
                  <Button variant="primary" size="sm" icon="plus" onClick={onNew}>
                    {newLabel}
                  </Button>
                ) : undefined
              }
            />
          </div>
        ) : (
          <Table
            columns={columns}
            rows={filtered}
            rowKey={rowKey}
            onRowClick={open}
            selectedKey={filtered[selected] ? rowKey(filtered[selected]!) : undefined}
          />
        )}
      </Card>
    </div>
  );
}

function ListSkeleton({ columns }: { columns: number }) {
  return (
    <div className="pf-pad col gap8" aria-busy="true" aria-label="Loading">
      {Array.from({ length: 6 }).map((_, rowIndex) => (
        <div key={rowIndex} className="row gap12">
          {Array.from({ length: columns }).map((__, colIndex) => (
            <div key={colIndex} className="pf-skeleton" style={{ flex: colIndex === 0 ? 2 : 1 }} />
          ))}
        </div>
      ))}
    </div>
  );
}
