import { http, HttpResponse } from 'msw';

// Default §C.6 handlers — baseline is logged-out (GET /me → 401). Tests override per scenario with
// server.use(...). WP-08 builds against these; the Integration Gate flips dev to the real API.
export const handlers = [
  http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
  // The shape the cookie handler really writes for an unauthenticated /api request (ADR-025,
  // 2026-09-10 addendum 2). `useSession` reads only the status, but a fixture is a claim about the
  // server, so it states what the server actually sends rather than a bare status.
  http.get('/api/auth/me', () =>
    HttpResponse.json(
      {
        title: 'not_authenticated',
        status: 401,
        detail: 'Not authenticated.',
        code: 'not_authenticated',
        correlationId: '00000000000000000000000000000000',
      },
      { status: 401, headers: { 'content-type': 'application/problem+json' } },
    ),
  ),
  http.post('/api/auth/login', () => HttpResponse.json({ status: 'ok', mfaToken: null })),
  http.post('/api/auth/mfa', () => HttpResponse.json({ status: 'ok', mfaToken: null })),
  http.post('/api/auth/logout', () => new HttpResponse(null, { status: 204 })),
];
