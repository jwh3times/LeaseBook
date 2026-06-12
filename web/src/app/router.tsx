import { createBrowserRouter, Navigate } from 'react-router-dom';
import { KitchenSink } from '@/dev/KitchenSink';
import { LoginPage } from '@/features/auth/LoginPage';
import { AppShell } from './AppShell';
import { NAV_ROUTES, SETTINGS_ROUTE } from './navigation';
import { NotFound } from './NotFound';
import { PlaceholderPage } from './PlaceholderPage';
import { RouteGuard } from './RouteGuard';

const pageRoutes = [...NAV_ROUTES, SETTINGS_ROUTE].map((route) => ({
  path: route.path,
  element: <PlaceholderPage title={route.title} />,
}));

export const router = createBrowserRouter([
  { path: '/login', element: <LoginPage /> },
  { path: '/dev/kitchen-sink', element: <KitchenSink /> },
  {
    element: <RouteGuard />,
    children: [
      {
        element: <AppShell />,
        children: [{ index: true, element: <Navigate to="/dashboard" replace /> }, ...pageRoutes],
      },
    ],
  },
  { path: '*', element: <NotFound /> },
]);
