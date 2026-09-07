import { Navigate, Outlet, useLocation } from 'react-router';
import { useSession } from '@/features/auth/useSession';

/** Redirects unauthenticated users to /login; renders the protected tree otherwise. */
export function RouteGuard() {
  const location = useLocation();
  const { data: session, isLoading } = useSession();

  if (isLoading) {
    return <div className="pf-page t3">Loading…</div>;
  }
  if (!session) {
    return <Navigate to="/login" replace />;
  }
  if (session.mfaEnrollmentRequired && location.pathname !== '/account/security') {
    return <Navigate to="/account/security" replace />;
  }
  return <Outlet />;
}
