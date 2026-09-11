import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, RouterProvider } from 'react-router';
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

  it('accepts a recovery code after the password step', async () => {
    server.use(
      http.post('/api/auth/login', () =>
        HttpResponse.json({ status: 'mfa-required', mfaToken: 'tok-123' }),
      ),
      http.post('/api/auth/mfa/recovery', () =>
        HttpResponse.json({ status: 'ok', mfaToken: null }),
      ),
    );
    await fillCredentials();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Use a recovery code' }));
    await user.type(screen.getByLabelText('Recovery code'), 'recovery-example');
    await user.click(screen.getByRole('button', { name: 'Verify' }));
    expect(await screen.findByText('dashboard ready')).toBeInTheDocument();
  });

  it('surfaces an error on invalid credentials', async () => {
    server.use(http.post('/api/auth/login', () => new HttpResponse(null, { status: 401 })));
    await fillCredentials();
    expect(await screen.findByText(/invalid email or password/i)).toBeInTheDocument();
  });

  // #360. The page used to `catch {}` and substitute its own literal on both steps, so a 500, a
  // dropped connection, an expired sign-in attempt and a wrong password all rendered identically —
  // and a failed sign-in was the one operator failure in the product with no reference to quote.
  it('shows the reason and a support reference when the sign-in attempt could not be completed', async () => {
    server.use(
      http.post('/api/auth/login', () =>
        HttpResponse.json({ status: 'mfa-required', mfaToken: 'tok-123' }),
      ),
      http.post('/api/auth/mfa', () =>
        HttpResponse.json(
          {
            code: 'mfa_session_expired',
            detail: 'Your sign-in attempt timed out. Start again from the sign-in page.',
            correlationId: 'abc123def456',
          },
          { status: 401 },
        ),
      ),
    );
    await fillCredentials();
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Authentication code'), '123456');
    await user.click(screen.getByRole('button', { name: 'Verify' }));

    expect(await screen.findByText(/timed out/i)).toBeInTheDocument();
    expect(screen.getByText(/Reference: abc123def456/)).toBeInTheDocument();
    // The wrong answer this replaced.
    expect(screen.queryByText(/invalid authentication code/i)).not.toBeInTheDocument();

    // And the copy must land on the screen it names. Leaving the user on the MFA step gave them a
    // Verify button that re-issues the same 401 forever, while being told to start again from a
    // page they could not reach — #357's dead end with the copy now pointing at the missing exit.
    expect(screen.getByLabelText('Email')).toBeInTheDocument();
    expect(screen.getByLabelText('Password')).toBeInTheDocument();
    expect(screen.queryByLabelText('Authentication code')).not.toBeInTheDocument();
  });

  it('names the condition when the server cannot be reached', async () => {
    server.use(http.post('/api/auth/login', () => HttpResponse.error()));
    await fillCredentials();
    expect(await screen.findByText(/could not reach the server/i)).toBeInTheDocument();
    expect(screen.queryByText(/invalid email or password/i)).not.toBeInTheDocument();
  });

  it('names the condition when sign-in is rate limited', async () => {
    // The rate limiter writes a bare 429 carrying only Retry-After, so there is no body to render
    // and no reference to quote — the page has to supply the words itself.
    server.use(http.post('/api/auth/login', () => new HttpResponse(null, { status: 429 })));
    await fillCredentials();
    expect(await screen.findByText(/too many sign-in attempts/i)).toBeInTheDocument();
    expect(screen.queryByText(/invalid email or password/i)).not.toBeInTheDocument();
  });

  // A 400 is the request failing around the credential judgement, not a judgement. Blaming the
  // password for it sends the user to retype something that was never wrong, and a repeated
  // antiforgery rejection would loop there indefinitely.
  it('does not blame the credentials for a rejected request', async () => {
    server.use(
      http.post('/api/auth/login', () =>
        HttpResponse.json(
          {
            code: 'antiforgery_rejected',
            detail: 'The request could not be verified. Reload and try again.',
            correlationId: 'csrf-ref',
          },
          { status: 400 },
        ),
      ),
    );
    await fillCredentials();
    expect(await screen.findByText(/could not be verified/i)).toBeInTheDocument();
    expect(screen.getByText(/Reference: csrf-ref/)).toBeInTheDocument();
    expect(screen.queryByText(/invalid email or password/i)).not.toBeInTheDocument();
  });

  it('keeps a wrong recovery code generic too', async () => {
    server.use(
      http.post('/api/auth/login', () =>
        HttpResponse.json({ status: 'mfa-required', mfaToken: 'tok-123' }),
      ),
      http.post('/api/auth/mfa/recovery', () =>
        HttpResponse.json(
          {
            code: 'invalid_recovery_code',
            detail: 'Invalid recovery code.',
            correlationId: 'rec-ref',
          },
          { status: 401 },
        ),
      ),
    );
    await fillCredentials();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Use a recovery code' }));
    await user.type(screen.getByLabelText('Recovery code'), 'recovery-example');
    await user.click(screen.getByRole('button', { name: 'Verify' }));

    expect(await screen.findByText(/invalid recovery code/i)).toBeInTheDocument();
    expect(screen.queryByText(/rec-ref/)).not.toBeInTheDocument();
  });

  it('does not blame the credentials for a server fault', async () => {
    server.use(
      http.post('/api/auth/login', () =>
        HttpResponse.json(
          { code: 'internal_error', detail: 'Boom.', correlationId: 'srv500ref' },
          { status: 500 },
        ),
      ),
    );
    await fillCredentials();
    expect(await screen.findByText(/Reference: srv500ref/)).toBeInTheDocument();
    expect(screen.queryByText(/invalid email or password/i)).not.toBeInTheDocument();
  });

  // The property most at risk from the change: a judged credential must reveal nothing, so the
  // server's own detail and its reference must NOT reach the page even though the contract carries
  // them. This is the one surface where honouring ADR-025 fully would be an enumeration regression.
  it('keeps a rejected credential generic, with no server detail and no reference', async () => {
    server.use(
      http.post('/api/auth/login', () =>
        HttpResponse.json(
          {
            code: 'invalid_credentials',
            detail: 'No account exists for that email address.',
            correlationId: 'leak-me-if-you-can',
          },
          { status: 401 },
        ),
      ),
    );
    await fillCredentials();
    expect(await screen.findByText(/invalid email or password/i)).toBeInTheDocument();
    expect(screen.queryByText(/No account exists/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/leak-me-if-you-can/)).not.toBeInTheDocument();
  });

  it('keeps a wrong authenticator code generic too', async () => {
    server.use(
      http.post('/api/auth/login', () =>
        HttpResponse.json({ status: 'mfa-required', mfaToken: 'tok-123' }),
      ),
      http.post('/api/auth/mfa', () =>
        HttpResponse.json(
          { code: 'invalid_mfa_code', detail: 'Invalid code.', correlationId: 'mfa-ref' },
          { status: 401 },
        ),
      ),
    );
    await fillCredentials();
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Authentication code'), '999999');
    await user.click(screen.getByRole('button', { name: 'Verify' }));

    expect(await screen.findByText(/invalid authentication code/i)).toBeInTheDocument();
    expect(screen.queryByText(/mfa-ref/)).not.toBeInTheDocument();
  });

  beforeEach(() => {
    renderLogin();
  });
});
