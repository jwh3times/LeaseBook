import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { RouteGuard } from './RouteGuard';

function renderGuarded() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      {
        element: <RouteGuard />,
        children: [{ path: '/dashboard', element: <div>protected content</div> }],
      },
      { path: '/login', element: <div>login screen</div> },
    ],
    { initialEntries: ['/dashboard'] },
  );
  render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

describe('RouteGuard', () => {
  it('renders the protected route for an authenticated session', async () => {
    server.use(
      http.get('/api/auth/me', () =>
        HttpResponse.json({
          userId: '00000000-0000-0000-0000-000000000001',
          name: 'Renée Calloway',
          email: 'renee@example.com',
          role: 'PMAdmin',
          orgId: '00000000-0000-0000-0000-0000000000aa',
          orgName: 'Tarheel Property Group',
        }),
      ),
    );
    renderGuarded();
    expect(await screen.findByText('protected content')).toBeInTheDocument();
  });

  it('redirects to /login when there is no session', async () => {
    // Default handler returns 401.
    renderGuarded();
    expect(await screen.findByText('login screen')).toBeInTheDocument();
  });
});
