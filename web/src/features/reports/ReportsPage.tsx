import { useState } from 'react';
import { useParams } from 'react-router-dom';
import { Card, EmptyState } from '@/design';
import { ReportCatalog } from './ReportCatalog';
import { OwnerStatementView } from './OwnerStatementView';
import { currentPeriodFilters, type StatementFilters, useStatement } from './reports';
import './reports.css';

// ---- OwnerStatementPage -----------------------------------------------------
// Rendered at /owners/:id/statement (or /statements/:id from deep-link)

interface OwnerStatementPageProps {
  ownerId: string;
}

function OwnerStatementPage({ ownerId }: OwnerStatementPageProps) {
  const [filters, setFilters] = useState<StatementFilters>(currentPeriodFilters);
  const statement = useStatement(ownerId, filters);

  if (statement.isPending) {
    return (
      <div className="pf-fade">
        <div className="pf-pagehd">
          <div>
            <h2>Owner statement</h2>
          </div>
        </div>
        <div className="pf-stmt-layout">
          <Card pad>
            <div className="col gap8">
              {[0, 1, 2, 3, 4, 5].map((i) => (
                <div key={i} className="pf-skeleton" style={{ height: 24 }} />
              ))}
            </div>
          </Card>
          <div className="col" style={{ gap: 'var(--gap)' }}>
            <Card pad>
              <div className="pf-skeleton" style={{ height: 120 }} />
            </Card>
            <Card pad>
              <div className="pf-skeleton" style={{ height: 80 }} />
            </Card>
          </div>
        </div>
      </div>
    );
  }

  if (statement.isError || !statement.data) {
    return (
      <div className="pf-fade">
        <div className="pf-pagehd">
          <div>
            <h2>Owner statement</h2>
          </div>
        </div>
        <Card pad>
          <EmptyState
            icon="alert"
            title="Couldn't load the statement"
            description="Please retry in a moment."
          />
        </Card>
      </div>
    );
  }

  return (
    <OwnerStatementView
      ownerId={ownerId}
      statement={statement.data}
      filters={filters}
      onFiltersChange={setFilters}
    />
  );
}

// ---- ReportsPage ------------------------------------------------------------
// Renders at /reports — the catalog is the default view.
// A future route /owners/:id/statement will mount OwnerStatementPage directly.

export function ReportsPage() {
  return <ReportCatalog />;
}

// Exported separately for the /owners/:id route (wired in the router below).
export function StatementPage() {
  const { id } = useParams<{ id: string }>();
  if (!id) {
    return (
      <div className="pf-fade">
        <Card pad>
          <EmptyState
            icon="alert"
            title="Invalid statement URL"
            description="No owner ID supplied."
          />
        </Card>
      </div>
    );
  }
  return <OwnerStatementPage ownerId={id} />;
}
