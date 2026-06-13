import type { UseQueryResult } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { useNavigate } from 'react-router-dom';
import { Button, Card, EmptyState } from '@/design';
import { RecordQuickSwitch } from './RecordQuickSwitch';
import type { EntityKind } from './recordNav';

interface DetailPageProps<T> {
  kind: EntityKind;
  id: string;
  query: UseQueryResult<T | undefined>;
  backTo: string;
  backLabel: string;
  toPath: (id: string) => string;
  title: (data: T) => ReactNode;
  sub?: (data: T) => ReactNode;
  actions?: (data: T) => ReactNode;
  children: (data: T) => ReactNode;
}

/** Detail scaffold: back link, title/sub, the record quick-switcher, and loading/not-found states. */
export function DetailPage<T>({
  kind, id, query, backTo, backLabel, toPath, title, sub, actions, children,
}: DetailPageProps<T>) {
  const navigate = useNavigate();
  const data = query.data;

  return (
    <div className="pf-fade">
      <div className="row gap8 mb16">
        <Button variant="ghost" size="sm" icon="chevronLeft" onClick={() => navigate(backTo)}>{backLabel}</Button>
        <RecordQuickSwitch kind={kind} currentId={id} toPath={toPath} />
      </div>

      {query.isPending ? (
        <Card pad><div className="pf-skeleton" style={{ maxWidth: 260, height: 22 }} /></Card>
      ) : query.isError || !data ? (
        <Card pad><EmptyState icon="alert" title="Record not found" description="It may have been removed, or the link is wrong." /></Card>
      ) : (
        <>
          <div className="pf-detailhd">
            <div>
              <h2>{title(data)}</h2>
              {sub && <p className="sub">{sub(data)}</p>}
            </div>
            {actions && <div className="row gap8">{actions(data)}</div>}
          </div>
          {children(data)}
        </>
      )}
    </div>
  );
}
