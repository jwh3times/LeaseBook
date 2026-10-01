import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { PortalEnrollmentPage } from './PortalEnrollmentPage';

function renderEnrollment() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [{ path: '/portal/enroll', element: <PortalEnrollmentPage /> }],
    { initialEntries: ['/portal/enroll#token=private-invitation-proof'] },
  );
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return { client, router };
}

it('enrolls a new recipient without storing invitation proof or password in the query cache or URL', async () => {
  let accepted: unknown;
  server.use(
    http.post('/api/portal-enrollment/inspect', () =>
      HttpResponse.json({
        email: 'invitee@example.test',
        displayName: 'Invited Person',
        persona: 'tenant',
        requiresSignIn: false,
        requiresPassword: true,
      }),
    ),
    http.post('/api/portal-enrollment/accept', async ({ request }) => {
      accepted = await request.json();
      return HttpResponse.json({ persona: 'tenant' });
    }),
  );
  const { client, router } = renderEnrollment();
  const user = userEvent.setup();
  await user.type(await screen.findByLabelText('New password'), 'Portal-Trust-2026!');
  await user.click(screen.getByRole('button', { name: 'Accept invitation' }));
  expect(await screen.findByText('Your portal access is ready.')).toBeInTheDocument();
  expect(accepted).toEqual({
    token: 'private-invitation-proof',
    displayName: 'Invited Person',
    password: 'Portal-Trust-2026!',
  });
  expect(router.state.location.hash).toBe('');
  expect(screen.queryByLabelText('New password')).not.toBeInTheDocument();
  expect(JSON.stringify(client.getQueryCache().getAll())).not.toContain('private-invitation-proof');
  expect(JSON.stringify(client.getMutationCache().getAll())).not.toContain('Portal-Trust-2026!');
});

it.each(['unavailable', 'incompatible_session'])(
  'recovers from %s without discarding the invitation',
  async (failure) => {
    let recovered = false;
    server.use(
      http.post('/api/portal-enrollment/inspect', () => {
        if (!recovered)
          return HttpResponse.json(
            {
              code: failure,
              detail:
                failure === 'incompatible_session'
                  ? 'Sign out before opening this invitation.'
                  : 'Temporarily unavailable.',
              correlationId: 'inspect-reference',
            },
            { status: failure === 'incompatible_session' ? 409 : 503 },
          );
        return HttpResponse.json({
          email: 'recovered@example.test',
          displayName: 'Person',
          persona: 'tenant',
          requiresSignIn: false,
          requiresPassword: true,
        });
      }),
      http.post('/api/auth/logout', () => {
        recovered = true;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    renderEnrollment();
    expect(await screen.findByText('Reference: inspect-reference')).toBeInTheDocument();
    if (failure === 'unavailable') recovered = true;
    await userEvent.click(
      screen.getByRole('button', {
        name: failure === 'incompatible_session' ? 'Sign out everywhere and continue' : 'Retry',
      }),
    );
    expect(await screen.findByText('recovered@example.test')).toBeInTheDocument();
  },
);

it('uses a replacement invitation opened in the same tab', async () => {
  server.use(
    http.post('/api/portal-enrollment/inspect', async ({ request }) => {
      const { token } = (await request.json()) as { token: string };
      return HttpResponse.json({
        email: token === 'replacement-proof' ? 'replacement@example.test' : 'original@example.test',
        displayName: 'Person',
        persona: 'tenant',
        requiresSignIn: false,
        requiresPassword: true,
      });
    }),
  );
  const { router } = renderEnrollment();
  expect(await screen.findByText('original@example.test')).toBeInTheDocument();
  await act(async () => {
    await router.navigate('/portal/enroll#token=replacement-proof');
  });
  expect(await screen.findByText('replacement@example.test')).toBeInTheDocument();
  expect(screen.queryByText('original@example.test')).not.toBeInTheDocument();
  expect(router.state.location.hash).toBe('');
});

it('uses ordinary password and MFA sign-in for an existing account without setting a new password', async () => {
  let signedIn = false;
  let accepted: unknown;
  server.use(
    http.post('/api/portal-enrollment/inspect', () =>
      HttpResponse.json({
        email: 'existing@example.test',
        displayName: 'Existing Person',
        persona: 'owner',
        requiresSignIn: !signedIn,
        requiresPassword: false,
      }),
    ),
    http.post('/api/auth/login', () =>
      HttpResponse.json({ status: 'mfa-required', mfaToken: 'partial-session' }),
    ),
    http.post('/api/auth/mfa', () => {
      signedIn = true;
      return HttpResponse.json({ status: 'ok', mfaToken: null });
    }),
    http.post('/api/portal-enrollment/accept', async ({ request }) => {
      accepted = await request.json();
      return HttpResponse.json({ persona: 'owner' });
    }),
  );
  renderEnrollment();
  const user = userEvent.setup();
  await user.click(await screen.findByRole('button', { name: 'Sign in to accept' }));
  await user.type(screen.getByLabelText('Email'), 'existing@example.test');
  await user.type(screen.getByLabelText('Password'), 'Existing-Password-2026!');
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
  await user.type(await screen.findByLabelText('Authentication code'), '123456');
  await user.click(screen.getByRole('button', { name: 'Verify' }));
  await user.click(await screen.findByRole('button', { name: 'Accept invitation' }));
  expect(await screen.findByText('Your portal access is ready.')).toBeInTheDocument();
  expect(accepted).toEqual({ token: 'private-invitation-proof' });
  expect(screen.queryByLabelText('New password')).not.toBeInTheDocument();
});
