import type { IconName, NavItem } from '@/design';

export interface NavRoute {
  item: NavItem;
  path: string;
  title: string;
}

function route(id: string, label: string, icon: IconName, path: string, title: string): NavRoute {
  return { item: { id, label, icon }, path, title };
}

// Primary navigation. Pages are titled placeholders in M0; feature milestones fill them in.
export const NAV_ROUTES: NavRoute[] = [
  route('dashboard', 'Dashboard', 'dashboard', '/dashboard', 'Dashboard'),
  route('tenants', 'Tenants', 'tenants', '/tenants', 'Tenant Ledger'),
  route('owners', 'Owners', 'owners', '/owners', 'Owner Statements'),
  route('properties', 'Properties', 'building', '/properties', 'Properties'),
  route('banking', 'Banking', 'bank', '/banking', 'Banking & Reconciliation'),
  route('reports', 'Reports', 'reports', '/reports', 'Reports'),
  route('operations', 'Operations', 'refresh', '/operations', 'Operations'),
];

/**
 * Primary navigation that only a PMAdmin sees. Separate from {@link NAV_ROUTES} rather than carrying
 * a flag on each item, so "which routes need a role" is one list to read — and so the default for a
 * new route stays "everyone", which is the safe direction for a nav item and the wrong one for an
 * endpoint. The endpoint is what actually enforces this; hiding the item only keeps staff from
 * walking into a 403.
 */
export const ADMIN_NAV_ROUTES: NavRoute[] = [
  route('audit', 'Audit log', 'clock', '/audit', 'Audit Log'),
];

export const SETTINGS_ROUTE: NavRoute = route(
  'settings',
  'Settings',
  'settings',
  '/settings',
  'Settings',
);

/** Every route the shell can title, whether or not this user can see its nav item. */
export const ALL_NAV_ROUTES: NavRoute[] = [...NAV_ROUTES, ...ADMIN_NAV_ROUTES, SETTINGS_ROUTE];
