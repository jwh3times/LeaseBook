import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { beforeEach, describe, expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { LoginPage } from './LoginPage';

function renderLogin() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      { path: '/login', element: <LoginPage /> },
      { path: '/dashboard', element: <div>dashboard ready</div> },
    ],
    { initialEntries: ['/login'] },
  );
  render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

async function fillCredentials() {
  const user = userEvent.setup();
  await user.type(screen.getByLabelText('Email'), 'admin@example.com');
  await user.type(screen.getByLabelText('Password'), 'Tarheel-Trust-2026!');
  await user.click(screen.getByRole('button', { name: /sign in/i }));
}

describe('LoginPage', () => {
  it('navigates to the dashboard on a successful password login', async () => {
    await fillCredentials();
    expect(await screen.findByText('dashboard ready')).toBeInTheDocument();
  });

  it('shows the MFA step when the server requires a second factor', async () => {
    server.use(
      http.post('/api/auth/login', () =>
        HttpResponse.json({ status: 'mfa-required', mfaToken: 'tok-123' }),
      ),
    );
    await fillCredentials();
    expect(await screen.findByLabelText('Authentication code')).toBeInTheDocument();
  });

  it('surfaces an error on invalid credentials', async () => {
    server.use(http.post('/api/auth/login', () => new HttpResponse(null, { status: 401 })));
    await fillCredentials();
    expect(await screen.findByText(/invalid email or password/i)).toBeInTheDocument();
  });

  beforeEach(() => {
    renderLogin();
  });
});
