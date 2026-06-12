import { http, HttpResponse } from 'msw';

// Default §C.6 handlers — baseline is logged-out (GET /me → 401). Tests override per scenario with
// server.use(...). WP-08 builds against these; the Integration Gate flips dev to the real API.
export const handlers = [
  http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
  http.get('/api/auth/me', () => new HttpResponse(null, { status: 401 })),
  http.post('/api/auth/login', () => HttpResponse.json({ status: 'ok', mfaToken: null })),
  http.post('/api/auth/mfa', () => HttpResponse.json({ status: 'ok', mfaToken: null })),
  http.post('/api/auth/logout', () => new HttpResponse(null, { status: 204 })),
];
