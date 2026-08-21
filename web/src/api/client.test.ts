import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import './client';
import { postApiAuthLogin } from './generated';

// The absorbed XSRF policy (#237): mutations carry the token without call sites priming first.
// These tests drive a real generated POST through the client interceptors against MSW.

function clearCsrfCookie() {
  document.cookie = 'XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
}

function setCsrfCookie(value: string) {
  document.cookie = `XSRF-TOKEN=${value}; path=/`;
}

/** Overrides GET /api/auth/csrf to issue `token` into the cookie jar, counting calls. */
function issueOnPrime(token: string): { calls: number } {
  const counter = { calls: 0 };
  server.use(
    http.get('/api/auth/csrf', () => {
      counter.calls += 1;
      setCsrfCookie(token);
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return counter;
}

const ANTIFORGERY_REJECTED = {
  title: 'antiforgery_rejected',
  detail: 'Invalid or missing antiforgery token.',
};

describe('XSRF absorbed into the request path', () => {
  beforeEach(() => {
    clearCsrfCookie();
  });

  it('primes an unprimed session and sends the issued token', async () => {
    const primes = issueOnPrime('issued-token');
    const seenTokens: (string | null)[] = [];
    server.use(
      http.post('/api/auth/login', ({ request }) => {
        seenTokens.push(request.headers.get('X-XSRF-TOKEN'));
        return HttpResponse.json({ status: 'ok', mfaToken: null });
      }),
    );

    const { data } = await postApiAuthLogin({ body: { email: 'a@b.c', password: 'pw' } });

    expect(data).toMatchObject({ status: 'ok' });
    expect(primes.calls).toBe(1);
    expect(seenTokens).toEqual(['issued-token']);
  });

  it('sends the existing cookie without a priming round-trip', async () => {
    const primes = issueOnPrime('never-issued');
    setCsrfCookie('existing-token');
    const seenTokens: (string | null)[] = [];
    server.use(
      http.post('/api/auth/login', ({ request }) => {
        seenTokens.push(request.headers.get('X-XSRF-TOKEN'));
        return HttpResponse.json({ status: 'ok', mfaToken: null });
      }),
    );

    await postApiAuthLogin({ body: { email: 'a@b.c', password: 'pw' } });

    expect(primes.calls).toBe(0);
    expect(seenTokens).toEqual(['existing-token']);
  });

  it('shares one prime across concurrent unprimed mutations', async () => {
    const primes = issueOnPrime('shared-token');
    server.use(
      http.post('/api/auth/login', () => HttpResponse.json({ status: 'ok', mfaToken: null })),
    );

    await Promise.all([
      postApiAuthLogin({ body: { email: 'a@b.c', password: 'pw' } }),
      postApiAuthLogin({ body: { email: 'a@b.c', password: 'pw' } }),
    ]);

    expect(primes.calls).toBe(1);
  });

  // The token is user-bound, so a present cookie can still be stale — minted for a different
  // signed-in state. Presence checks cannot catch that; the server's stable code can.
  it('replays once with a fresh token when the server rejects a stale one', async () => {
    issueOnPrime('fresh-token');
    setCsrfCookie('stale-token');
    const attempts: { token: string | null; email: unknown }[] = [];
    server.use(
      http.post('/api/auth/login', async ({ request }) => {
        const body = (await request.json()) as { email?: unknown };
        attempts.push({ token: request.headers.get('X-XSRF-TOKEN'), email: body.email });
        if (attempts.length === 1) {
          return HttpResponse.json(ANTIFORGERY_REJECTED, { status: 400 });
        }
        return HttpResponse.json({ status: 'ok', mfaToken: null });
      }),
    );

    const { data } = await postApiAuthLogin({ body: { email: 'a@b.c', password: 'pw' } });

    expect(data).toMatchObject({ status: 'ok' });
    // The replay carried the refreshed token and the original body.
    expect(attempts).toEqual([
      { token: 'stale-token', email: 'a@b.c' },
      { token: 'fresh-token', email: 'a@b.c' },
    ]);
  });

  it('surfaces a persistent rejection instead of replaying again', async () => {
    issueOnPrime('fresh-token');
    setCsrfCookie('stale-token');
    let attempts = 0;
    server.use(
      http.post('/api/auth/login', () => {
        attempts += 1;
        return HttpResponse.json(ANTIFORGERY_REJECTED, { status: 400 });
      }),
    );

    const { error, response } = await postApiAuthLogin({
      body: { email: 'a@b.c', password: 'pw' },
    });

    expect(attempts).toBe(2);
    expect(response?.status).toBe(400);
    expect(error).toMatchObject({ title: 'antiforgery_rejected' });
  });

  it('leaves other 400s alone', async () => {
    setCsrfCookie('valid-token');
    let attempts = 0;
    server.use(
      http.post('/api/auth/login', () => {
        attempts += 1;
        return HttpResponse.json({ errors: { Email: ['Email is required.'] } }, { status: 400 });
      }),
    );

    const { response } = await postApiAuthLogin({ body: { email: '', password: 'pw' } });

    expect(attempts).toBe(1);
    expect(response?.status).toBe(400);
  });
});
