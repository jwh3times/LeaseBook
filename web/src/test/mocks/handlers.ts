import { http, HttpResponse } from 'msw';

/** A new organization's settings as the server answers them. */
export const ORG_SETTINGS = {
  accountingBasis: 'cash',
  moneyNegativeDisplay: 'minus',
  legalName: null,
  address: null,
  city: null,
  state: null,
  zip: null,
  phone: null,
  logoBlobRef: null,
  rentDueDay: 1,
  lateFeeGraceDays: 5,
  lateFeeKind: 'flat',
  lateFeeAmount: 50,
  lateFeeRateBps: 500,
  staffCanManagePortalAccess: true,
  cardFeeRateBps: 0,
  cardFeeFixed: 0,
  cardFeeCap: null,
  achFeeRateBps: 0,
  achFeeFixed: 0,
  achFeeCap: null,
  timeZone: 'America/New_York',
};

// Default §C.6 handlers — baseline is logged-out (GET /me → 401). Tests override per scenario with
// server.use(...). WP-08 builds against these; the Integration Gate flips dev to the real API.
export const handlers = [
  http.get('/api/portal-access/:persona/:targetId', () =>
    HttpResponse.json({ canManage: false, invitations: [], users: [] }),
  ),
  http.get('/api/portal/tenant/payments', () => HttpResponse.json({ enabled: false, items: [] })),
  http.get('/api/payments', () => HttpResponse.json({ enabled: false, items: [] })),
  http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
  // Every surface that dates a posting reads the organization's time zone from here (#642); a test
  // that needs other settings overrides this, starting from ORG_SETTINGS.
  http.get('/api/settings/org', () => HttpResponse.json(ORG_SETTINGS)),
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
