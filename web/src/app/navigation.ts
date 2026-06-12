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

export const SETTINGS_ROUTE: NavRoute = route('settings', 'Settings', 'settings', '/settings', 'Settings');
