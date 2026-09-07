import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import { RouteGuard } from '@/app/RouteGuard';
import { AccountSecurityPage } from './AccountSecurityPage';

vi.mock('qrcode', () => ({
  default: { toDataURL: () => Promise.resolve('data:image/png;base64,test') },
}));

function renderSecurity(required = true) {
  server.use(
    http.get('/api/auth/me', () =>
      HttpResponse.json({
        userId: 'user',
        role: 'PMAdmin',
        mfaEnabled: false,
        mfaEnrollmentRequired: required,
      }),
    ),
  );
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      {
        element: <RouteGuard />,
        children: [
          { path: '/dashboard', element: <div>Dashboard</div> },
          { path: '/account/security', element: <AccountSecurityPage /> },
        ],
      },
    ],
    { initialEntries: [required ? '/dashboard' : '/account/security'] },
  );
  render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return queryClient;
}

describe('Account security', () => {
  it('redirects required enrollment and keeps recovery codes out of query caches', async () => {
    let enabled = false;
    const client = renderSecurity();
    server.use(
      http.get('/api/auth/me', () =>
        HttpResponse.json({
          userId: 'user',
          role: 'PMAdmin',
          mfaEnabled: enabled,
          mfaEnrollmentRequired: !enabled,
        }),
      ),
      http.post('/api/auth/mfa/enroll', () =>
        HttpResponse.json({
          secret: 'TESTSETUPKEY',
          otpauthUri: 'otpauth://totp/test?secret=TESTSETUPKEY',
        }),
      ),
      http.post('/api/auth/mfa/enroll/confirm', () => {
        enabled = true;
        return HttpResponse.json({ codes: ['recovery-one', 'recovery-two'] });
      }),
    );
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Set up authenticator' }));
    expect(await screen.findByAltText('Authenticator setup QR code')).toBeInTheDocument();
    await user.type(screen.getByLabelText('Authentication code'), '123456');
    await user.click(screen.getByRole('button', { name: 'Confirm authenticator' }));
    expect(await screen.findByText('recovery-one')).toBeInTheDocument();
    expect(screen.queryByLabelText('Setup key')).not.toBeInTheDocument();
    expect(
      JSON.stringify(
        client
          .getQueryCache()
          .getAll()
          .map((q) => q.state.data),
      ),
    ).not.toContain('recovery-one');
    await user.click(screen.getByRole('button', { name: 'I have saved my recovery codes' }));
    expect(screen.queryByText('recovery-one')).not.toBeInTheDocument();
    expect(screen.getByText('Authenticator enabled.')).toBeInTheDocument();
  });

  it('shows a setup failure and allows retry', async () => {
    renderSecurity();
    server.use(
      http.post('/api/auth/mfa/enroll', () =>
        HttpResponse.json({ detail: 'Setup unavailable.' }, { status: 503 }),
      ),
    );
    await userEvent.click(await screen.findByRole('button', { name: 'Set up authenticator' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Setup unavailable.');
    expect(screen.getByRole('button', { name: 'Set up authenticator' })).toBeEnabled();
  });

  it('clears password fields after success', async () => {
    renderSecurity(false);
    server.use(
      http.post('/api/auth/change-password', () => new HttpResponse(null, { status: 204 })),
    );
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Current password'), 'OldPassword-123!');
    await user.type(screen.getByLabelText('New password'), 'NewPassword-123!');
    await user.click(screen.getByRole('button', { name: 'Change password' }));
    expect(await screen.findByRole('status')).toHaveTextContent('Password changed.');
    expect(screen.getByLabelText('Current password')).toHaveValue('');
    expect(screen.getByLabelText('New password')).toHaveValue('');
  });
});
