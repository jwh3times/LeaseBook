import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { describe, expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { AppShell } from './AppShell';

const RECENT_KEY = 'leasebook.palette.recent';

function renderAppShell() {
  server.use(
    http.get('/api/auth/me', () =>
      HttpResponse.json({
        userId: 'user',
        name: 'Renée Calloway',
        email: 'renee@example.com',
        role: 'PMAdmin',
        orgId: 'org',
        orgName: 'Tarheel Property Group',
      }),
    ),
    http.get('/api/settings/org', () =>
      HttpResponse.json({ accountingBasis: 'cash', moneyNegativeDisplay: 'minus' }),
    ),
  );
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      {
        path: '/',
        element: <AppShell />,
        children: [{ index: true, element: <div>Dashboard</div> }],
      },
      { path: '/login', element: <div>Login</div> },
    ],
    { initialEntries: ['/'] },
  );
  render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

describe('AppShell', () => {
  it('clears command-palette recents after a successful sign out', async () => {
    localStorage.setItem(RECENT_KEY, '[{"type":"tenant","id":"tenant-1"}]');
    renderAppShell();

    await userEvent.click(await screen.findByRole('button', { name: 'Sign out' }));

    await waitFor(() => expect(localStorage.getItem(RECENT_KEY)).toBeNull());
    expect(await screen.findByText('Login')).toBeInTheDocument();
  });
});
