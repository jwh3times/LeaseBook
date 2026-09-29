import { Link, Navigate, Outlet } from 'react-router';
import { useSession } from '@/features/auth/useSession';

export type Persona = 'staff' | 'tenant' | 'owner';

function homeForRole(role: string | null | undefined) {
  return role === 'Tenant'
    ? '/portal/tenant'
    : role === 'Owner'
      ? '/portal/owner'
      : role === 'PMAdmin' || role === 'PMStaff'
        ? '/dashboard'
        : '/account/security';
}

function admits(persona: Persona, role: string | null | undefined): boolean {
  switch (persona) {
    case 'tenant':
      return role === 'Tenant';
    case 'owner':
      return role === 'Owner';
    case 'staff':
      return role === 'PMAdmin' || role === 'PMStaff';
  }
}

export function HomeRedirect() {
  const { data: session } = useSession();
  return <Navigate to={homeForRole(session?.role)} replace />;
}

/**
 * Runs above each persona's shell, so a denied navigation cannot mount another persona's query
 * hooks. The server is the enforcement; this keeps a wrong-persona link from asking for the data.
 */
export function PersonaGuard({ persona }: { persona: Persona }) {
  const { data: session } = useSession();
  if (admits(persona, session?.role)) return <Outlet />;
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
