import { Link } from 'react-router-dom';
import { Button, EmptyState } from '@/design';

export function NotFound() {
  return (
    <div className="pf-page">
      <EmptyState
        icon="alert"
        title="Page not found"
        description="The page you're looking for doesn't exist or has moved."
        action={
          <Link to="/dashboard">
            <Button variant="soft">Back to dashboard</Button>
          </Link>
        }
      />
    </div>
  );
}
