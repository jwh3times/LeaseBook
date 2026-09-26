import { Link, Navigate, Outlet } from 'react-router';
import { useSession } from '@/features/auth/useSession';

function homeForRole(role: string | null | undefined) {
  return role === 'Tenant'
    ? '/portal/tenant'
    : role === 'PMAdmin' || role === 'PMStaff'
      ? '/dashboard'
      : '/account/security';
}

export function HomeRedirect() {
  const { data: session } = useSession();
  return <Navigate to={homeForRole(session?.role)} replace />;
}

/** Runs above the staff shell, so denied navigation cannot mount staff query hooks. */
export function PersonaGuard({ persona }: { persona: 'staff' | 'tenant' }) {
  const { data: session } = useSession();
  const allowed =
    persona === 'tenant'
      ? session?.role === 'Tenant'
      : session?.role === 'PMAdmin' || session?.role === 'PMStaff';
  if (allowed) return <Outlet />;
  return (
    <main className="pf-page col gap16">
      <h1>Access denied</h1>
      <p>This page is not available for your account.</p>
      <Link to={homeForRole(session?.role)} style={{ color: 'var(--text)' }}>
        Return to your account
      </Link>
    </main>
  );
}
