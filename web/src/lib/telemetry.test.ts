import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { trackInteraction } from './telemetry';

describe('trackInteraction', () => {
  it('posts a tags-only budget sample and swallows failures', async () => {
    let resolveRequest!: (request: Request) => void;
    const capturedRequest = new Promise<Request>((resolve) => {
      resolveRequest = resolve;
    });
    server.use(
      http.post('/api/telemetry/budget', ({ request }) => {
        resolveRequest(request);
        return new HttpResponse(null, { status: 204 });
      }),
    );

    trackInteraction('entity-jump', 2, true);

    const request = await capturedRequest;
    expect(request.url).toContain('/api/telemetry/budget');
    expect(request.method).toBe('POST');
    expect(await request.json()).toEqual({
      task: 'entity-jump',
      interactions: 2,
      met: true,
    });
  });

  // Telemetry used to hand-roll credentials and the XSRF header over a raw fetch — a second copy of
  // transport policy that would have failed silently if the antiforgery contract ever moved. It now
  // goes through the generated client, so the interceptor supplies the header.
  it('echoes the XSRF cookie through the shared client interceptor', async () => {
    document.cookie = 'XSRF-TOKEN=tok-123';
    let resolveRequest!: (request: Request) => void;
    const capturedRequest = new Promise<Request>((resolve) => {
      resolveRequest = resolve;
    });
    server.use(
      http.post('/api/telemetry/budget', ({ request }) => {
        resolveRequest(request);
        return new HttpResponse(null, { status: 204 });
      }),
    );

    trackInteraction('record-payment', 3, true);

    const request = await capturedRequest;
    expect(request.headers.get('X-XSRF-TOKEN')).toBe('tok-123');
  });

  it('does not throw when the endpoint is unavailable', () => {
    server.use(http.post('/api/telemetry/budget', () => HttpResponse.error()));
    expect(() => trackInteraction('owner-balances-visible', 0, true)).not.toThrow();
  });
});
