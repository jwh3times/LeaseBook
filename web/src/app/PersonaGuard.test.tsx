import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { expect, it, vi } from 'vitest';
import { sessionQueryKey } from '@/features/auth/useSession';
import { HomeRedirect, PersonaGuard } from './PersonaGuard';

it.each(['Tenant', 'Owner'])(
  'denies %s staff navigation without mounting staff content',
  async (role) => {
    const staff = vi.fn(() => <div>staff data</div>);
    const queryClient = new QueryClient();
    queryClient.setQueryData(sessionQueryKey, { role });
    const router = createMemoryRouter(
      [
        {
          element: <PersonaGuard persona="staff" />,
          children: [{ path: '/dashboard', Component: staff }],
        },
      ],
      { initialEntries: ['/dashboard'] },
    );
    render(
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router} />
      </QueryClientProvider>,
    );
    expect(await screen.findByRole('heading', { name: 'Access denied' })).toBeInTheDocument();
    expect(staff).not.toHaveBeenCalled();
  },
);

it.each([
  ['Tenant', '/portal/tenant'],
  ['PMAdmin', '/dashboard'],
  ['PMStaff', '/dashboard'],
  ['Owner', '/account/security'],
])('routes %s root navigation to %s', async (role, destination) => {
  const queryClient = new QueryClient();
  queryClient.setQueryData(sessionQueryKey, { role });
  const router = createMemoryRouter([
    { path: '/', element: <HomeRedirect /> },
    { path: destination, element: <div>Destination</div> },
  ]);
  render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  await screen.findByText('Destination');
  expect(router.state.location.pathname).toBe(destination);
});
