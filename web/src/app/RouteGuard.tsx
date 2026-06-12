import { Navigate, Outlet } from 'react-router-dom';
import { useSession } from '@/features/auth/useSession';

/** Redirects unauthenticated users to /login; renders the protected tree otherwise. */
export function RouteGuard() {
  const { data: session, isLoading } = useSession();

  if (isLoading) {
    return <div className="pf-page t3">Loading…</div>;
  }
  if (!session) {
    return <Navigate to="/login" replace />;
  }
  return <Outlet />;
}
