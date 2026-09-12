import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Outlet, useLocation, useNavigate } from 'react-router';
import {
  AppLayout,
  Avatar,
  Button,
  IconButton,
  MoneyDisplayProvider,
  type NegativeStyle,
  Sidebar,
  Topbar,
} from '@/design';
import { signOutCurrentSession } from '@/features/auth/signOut';
import { sessionQueryKey, useSession } from '@/features/auth/useSession';
import { CommandPalette } from '@/features/palette/CommandPalette';
import { HelpOverlay } from '@/features/palette/HelpOverlay';
import { useOrgSettings } from '@/lib/settings';
import { useGlobalShortcuts } from '@/lib/useGlobalShortcuts';
import { ADMIN_NAV_ROUTES, ALL_NAV_ROUTES, NAV_ROUTES, SETTINGS_ROUTE } from './navigation';

function initialsOf(name: string): string {
  return name
    .split(' ')
    .map((part) => part.charAt(0))
    .filter(Boolean)
    .slice(0, 2)
    .join('')
    .toUpperCase();
}

export function AppShell() {
  const navigate = useNavigate();
  const location = useLocation();
  const queryClient = useQueryClient();
  const { data: session } = useSession();
  const { data: orgSettings } = useOrgSettings();
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);
  const negativeStyle = (orgSettings?.moneyNegativeDisplay ?? 'minus') as NegativeStyle;

  useGlobalShortcuts({
    onPalette: () => setPaletteOpen(true),
    onHelp: () => setHelpOpen(true),
    onNavigate: (path) => void navigate(path),
  });

  // Admin-only items appear once the session says so. A pending or failed session read shows the
  // shared nav rather than guessing a role — the endpoint is the enforcement, not this list.
  const navRoutes = session?.role === 'PMAdmin' ? [...NAV_ROUTES, ...ADMIN_NAV_ROUTES] : NAV_ROUTES;
  // Titled from every route, admin ones included: a PMStaff who follows a shared /audit link still
  // gets the page's own "admin access required", and a topbar reading "Dashboard" over it would be
  // the shell disagreeing with the page.
  const active =
    ALL_NAV_ROUTES.find((route) => location.pathname.startsWith(route.path)) ?? NAV_ROUTES[0]!;
  const displayName = session?.name ?? session?.email ?? 'User';

  async function signOut() {
    await signOutCurrentSession();
    await queryClient.invalidateQueries({ queryKey: sessionQueryKey });
    void navigate('/login', { replace: true });
  }

  return (
    <MoneyDisplayProvider negativeStyle={negativeStyle}>
      <AppLayout
        sidebar={
          <Sidebar
            brand={session?.orgName ?? 'LeaseBook'}
            items={navRoutes.map((route) => route.item)}
            activeId={active.item.id}
            onNavigate={(id) => {
              const target = navRoutes.find((route) => route.item.id === id);
              if (target) void navigate(target.path);
            }}
            onSettings={() => void navigate(SETTINGS_ROUTE.path)}
            user={{
              name: displayName,
              role: session?.role ?? '',
              initials: initialsOf(displayName),
            }}
          />
        }
        topbar={
          <Topbar
            title={active.title}
            onSearchClick={() => setPaletteOpen(true)}
            actions={
              <>
                <Button variant="primary" size="sm" icon="plus">
                  New
                </Button>
                <IconButton name="bell" label="Notifications" />
                <Avatar initials={initialsOf(displayName)} size={32} tone="var(--accent)" />
                <Button variant="ghost" size="sm" onClick={signOut}>
                  Sign out
                </Button>
              </>
            }
          />
        }
      >
        <Outlet />
      </AppLayout>
      {paletteOpen && <CommandPalette onClose={() => setPaletteOpen(false)} />}
      {helpOpen && <HelpOverlay onClose={() => setHelpOpen(false)} />}
    </MoneyDisplayProvider>
  );
}
