import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { MemoryRouter, Route, Routes } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { BankingPage } from '@/features/banking/BankingPage';
import { server } from '@/test/mocks/server';
import { RouteGuard } from './RouteGuard';

vi.mock('@/lib/telemetry', () => ({ trackInteraction: vi.fn() }));

const SIGNED_IN = {
  userId: 'u1',
  name: 'Pat Manager',
  email: 'pat@example.com',
  role: 'PMAdmin',
  orgId: 'o1',
  orgName: 'Demo Properties',
  mfaEnabled: true,
  mfaEnrollmentRequired: false,
};

/**
 * The exact state #357 lives in: the session cookie has expired, so every authorized read answers
 * 401, but `useSession`'s cached result still says "signed in" — so `RouteGuard` does not redirect.
 * Failing *both* reads is a different, already-handled state (RouteGuard redirects), which is why
 * `/api/auth/me` deliberately keeps succeeding here.
 */
const unauthorized = () =>
  HttpResponse.json(
    {
      type: 'https://tools.ietf.org/html/rfc9110#section-15.5.2',
      title: 'not_authenticated',
      status: 401,
      detail: 'Not authenticated.',
      code: 'not_authenticated',
      correlationId: '4b1d9f2c7a0e3d5186b4c9f0a2e7d318',
    },
    { status: 401, headers: { 'content-type': 'application/problem+json' } },
  );

function expiredSessionHandlers() {
  return [
    http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
    http.get('/api/auth/me', () => HttpResponse.json(SIGNED_IN)),
    // The exact shape the cookie handler now writes for an /api path with no valid cookie — see
    // ApiAwareProblem in AuthServiceCollectionExtensions.cs and the integration test that pins it
    // (AuthEndpointsTests.Unauthenticated_api_request_carries_the_error_contract). A fixture is a
    // claim about the server, so it is checked against the server's own factory rather than guessed.
    http.get('/api/accounting/*', () => unauthorized()),
    http.get('/api/directory/*', () => unauthorized()),
  ];
}

function renderGuarded() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/banking']}>
        <Routes>
          <Route element={<RouteGuard />}>
            <Route path="/banking" element={<BankingPage />} />
          </Route>
          <Route path="/login" element={<div>Login screen</div>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  document.body.innerHTML = '';
  vi.clearAllMocks();
});

describe('a session that expires while the tab is open', () => {
  it('tells the operator they are signed out instead of offering a retry that cannot work', async () => {
    server.use(...expiredSessionHandlers());
    renderGuarded();

    // Pin the premise: the guard still believes the user is signed in, so nothing redirected. If
    // this ever starts failing, the defect's state has moved and the assertions below prove nothing.
    const alerts = await screen.findAllByRole('alert');
    expect(screen.queryByText('Login screen')).not.toBeInTheDocument();
    expect(alerts.some((a) => /signed out/i.test(a.textContent ?? ''))).toBe(true);

    // The dead end itself: a Retry re-issues the same read and gets the same 401 forever.
    expect(screen.queryByRole('button', { name: /retry/i })).not.toBeInTheDocument();
    const signIn = screen.getAllByRole('link', { name: 'Sign in' })[0];
    expect(signIn).toHaveAttribute('href', '/login');

    // The support reference the surface could not show before, because the 401 had no body at all.
    expect(
      screen.getAllByText(/Reference: 4b1d9f2c7a0e3d5186b4c9f0a2e7d318/).length,
    ).toBeGreaterThan(0);
  });
});
