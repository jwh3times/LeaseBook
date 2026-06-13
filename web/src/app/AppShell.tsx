import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Outlet, useLocation, useNavigate } from 'react-router-dom';
import { api, primeCsrf } from '@/api';
import { AppLayout, Avatar, Button, IconButton, Sidebar, Topbar } from '@/design';
import { sessionQueryKey, useSession } from '@/features/auth/useSession';
import { CommandPalette } from '@/features/palette/CommandPalette';
import { HelpOverlay } from '@/features/palette/HelpOverlay';
import { useGlobalShortcuts } from '@/lib/useGlobalShortcuts';
import { NAV_ROUTES, SETTINGS_ROUTE } from './navigation';

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
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);

  useGlobalShortcuts({
    onPalette: () => setPaletteOpen(true),
    onHelp: () => setHelpOpen(true),
    onNavigate: navigate,
  });

  const active = NAV_ROUTES.find((route) => location.pathname.startsWith(route.path)) ?? NAV_ROUTES[0]!;
  const displayName = session?.name ?? session?.email ?? 'User';

  async function signOut() {
    await primeCsrf();
    await api.POST('/api/auth/logout');
    await queryClient.invalidateQueries({ queryKey: sessionQueryKey });
    navigate('/login', { replace: true });
  }

  return (
    <>
    <AppLayout
      sidebar={
        <Sidebar
          brand={session?.orgName ?? 'LeaseBook'}
          items={NAV_ROUTES.map((route) => route.item)}
          activeId={active.item.id}
          onNavigate={(id) => {
            const target = NAV_ROUTES.find((route) => route.item.id === id);
            if (target) navigate(target.path);
          }}
          onSettings={() => navigate(SETTINGS_ROUTE.path)}
          user={{ name: displayName, role: session?.role ?? '', initials: initialsOf(displayName) }}
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
    </>
  );
}
