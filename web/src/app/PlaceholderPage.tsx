import { EmptyState } from '@/design';

export interface PlaceholderPageProps {
  title: string;
  description?: string;
}

// Titled placeholder for each route until its feature milestone lands.
export function PlaceholderPage({ title, description }: PlaceholderPageProps) {
  return (
    <div className="pf-fade">
      <div className="pf-pagehd">
        <div>
          <h2>{title}</h2>
          <p>This section arrives in a later milestone.</p>
        </div>
      </div>
      <EmptyState
        icon="doc"
        title={`${title} — coming soon`}
        description={
          description ?? 'The screens for this area are built in a later milestone of the plan.'
        }
      />
    </div>
  );
}
