import { afterEach, describe, expect, it, vi } from 'vitest';
import { trackInteraction } from './telemetry';

describe('trackInteraction', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('posts a tags-only budget sample and swallows failures', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null, { status: 204 }));

    trackInteraction('entity-jump', 2, true);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(String(url)).toContain('/api/telemetry/budget');
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({ task: 'entity-jump', interactions: 2, met: true });
  });

  it('does not throw when the endpoint is unavailable', () => {
    vi.spyOn(globalThis, 'fetch').mockRejectedValue(new Error('network'));
    expect(() => trackInteraction('owner-balances-visible', 0, true)).not.toThrow();
  });
});
