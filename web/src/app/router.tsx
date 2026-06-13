import type { ReactElement } from 'react';
import { createBrowserRouter, Navigate } from 'react-router-dom';
import { KitchenSink } from '@/dev/KitchenSink';
import { LoginPage } from '@/features/auth/LoginPage';
import { DashboardPage } from '@/features/dashboard/DashboardPage';
import { OwnerDetailPage } from '@/features/owners/OwnerDetailPage';
import { OwnersPage } from '@/features/owners/OwnersPage';
import { PropertiesPage } from '@/features/properties/PropertiesPage';
import { PropertyDetailPage } from '@/features/properties/PropertyDetailPage';
import { SettingsPage } from '@/features/settings/SettingsPage';
import { TenantDetailPage } from '@/features/tenants/TenantDetailPage';
import { TenantsPage } from '@/features/tenants/TenantsPage';
import { AppShell } from './AppShell';
import { NAV_ROUTES, SETTINGS_ROUTE } from './navigation';
import { NotFound } from './NotFound';
import { PlaceholderPage } from './PlaceholderPage';
import { RouteGuard } from './RouteGuard';

// Routes whose feature screens have landed (others stay titled placeholders until their milestone).
const FEATURE_PAGES: Record<string, ReactElement> = {
  '/dashboard': <DashboardPage />,
  '/tenants': <TenantsPage />,
  '/owners': <OwnersPage />,
  '/properties': <PropertiesPage />,
  '/settings': <SettingsPage />,
};

const pageRoutes = [...NAV_ROUTES, SETTINGS_ROUTE].map((route) => ({
  path: route.path,
  element: FEATURE_PAGES[route.path] ?? <PlaceholderPage title={route.title} />,
}));

const detailRoutes = [
  { path: '/tenants/:id', element: <TenantDetailPage /> },
  { path: '/owners/:id', element: <OwnerDetailPage /> },
  { path: '/properties/:id', element: <PropertyDetailPage /> },
];

export const router = createBrowserRouter([
  { path: '/login', element: <LoginPage /> },
  { path: '/dev/kitchen-sink', element: <KitchenSink /> },
  {
    element: <RouteGuard />,
    children: [
      {
        element: <AppShell />,
        children: [{ index: true, element: <Navigate to="/dashboard" replace /> }, ...pageRoutes, ...detailRoutes],
      },
    ],
  },
  { path: '*', element: <NotFound /> },
]);
