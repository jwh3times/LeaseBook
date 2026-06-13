import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react';

export type EntityKind = 'tenants' | 'owners' | 'properties';

interface RecordNavState {
  order: Partial<Record<EntityKind, string[]>>;
  setOrder: (kind: EntityKind, ids: string[]) => void;
}

const RecordNavContext = createContext<RecordNavState | null>(null);

/**
 * Holds the current filtered list order per entity type so a detail view can move to the prev/next
 * record without returning to the index — the morning-payment-entry flow (Report §4.1, §C.7).
 */
export function RecordNavProvider({ children }: { children: ReactNode }) {
  const [order, setOrderState] = useState<Partial<Record<EntityKind, string[]>>>({});
  const setOrder = useCallback((kind: EntityKind, ids: string[]) => {
    setOrderState((current) => {
      const existing = current[kind];
      if (existing && existing.length === ids.length && existing.every((id, i) => id === ids[i])) {
        return current; // no change — avoid a re-render loop
      }
      return { ...current, [kind]: ids };
    });
  }, []);
  const value = useMemo(() => ({ order, setOrder }), [order, setOrder]);
  return <RecordNavContext.Provider value={value}>{children}</RecordNavContext.Provider>;
}

/** Records the current list order for an entity kind (called by the index page). */
export function useSetRecordOrder(kind: EntityKind): (ids: string[]) => void {
  const ctx = useContext(RecordNavContext);
  return useCallback((ids: string[]) => ctx?.setOrder(kind, ids), [ctx, kind]);
}

/** Prev/next ids around <code>currentId</code> in the list the user came from (called by the detail page). */
export function useRecordNav(kind: EntityKind, currentId: string): { prev?: string; next?: string } {
  const ctx = useContext(RecordNavContext);
  const ids = ctx?.order[kind] ?? [];
  const index = ids.indexOf(currentId);
  if (index < 0) return {};
  return {
    prev: index > 0 ? ids[index - 1] : undefined,
    next: index < ids.length - 1 ? ids[index + 1] : undefined,
  };
}
