import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { trackInteraction } from '@/lib/telemetry';
import { server } from '@/test/mocks/server';
import { useRunFlow } from './useRunFlow';

vi.mock('@/lib/telemetry', () => ({ trackInteraction: vi.fn() }));

const row = (targetId: string, extra: Record<string, unknown> = {}) => ({
  targetId,
  targetKind: 'Lease',
  label: targetId,
  amount: 50,
  alreadyDone: false,
  excludedReason: null,
  detail: {},
  ...extra,
});

const preview = (token: string, rows = [row('a'), row('b'), row('c')]) => ({
  runType: 'LateFee',
  year: 2026,
  month: 5,
  capabilitiesVersion: token,
  exceptions: [],
  rows,
});

const RESULT = {
  runId: 'run-1',
  runType: 'LateFee',
  year: 2026,
  month: 5,
  posted: 2,
  skipped: 0,
  excluded: 0,
  total: 100,
};

let queryClient: QueryClient;

function wrapper({ children }: { children: ReactNode }) {
  return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}

/** Serves `previews` in order (the last repeats), and records every confirm body. */
function serve(
  type: string,
  previews: object[],
  confirm: (n: number) => Response = () => HttpResponse.json(RESULT),
) {
  const bodies: Record<string, unknown>[] = [];
  let served = 0;
  server.use(
    http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
    http.get(`/api/operations/runs/${type}/preview`, () =>
      HttpResponse.json(previews[Math.min(served++, previews.length - 1)]),
    ),
    http.post(`/api/operations/runs/${type}/confirm`, async ({ request }) => {
      bodies.push((await request.json()) as Record<string, unknown>);
      return confirm(bodies.length);
    }),
  );
  return { bodies, previewsServed: () => served };
}

const conflict = () =>
  HttpResponse.json(
    { code: 'capabilities_changed', detail: 'The features changed.', correlationId: 'c1' },
    { status: 409 },
  );

const failure = () =>
  HttpResponse.json({ detail: 'The ledger is busy.', correlationId: 'f1' }, { status: 500 });

async function loaded(
  type: 'rent' | 'latefee' | 'disbursement',
  mode: 'selective' | 'all-eligible',
) {
  const hook = renderHook(() => useRunFlow(type, mode), { wrapper });
  await waitFor(() => expect(hook.result.current.preview.data).toBeDefined());
  return hook;
}

beforeEach(() => {
  queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  vi.clearAllMocks();
});

describe('useRunFlow — confirming', () => {
  it('posts the ticked targets with the preview token and reports the measured interactions', async () => {
    const { bodies } = serve('latefee', [preview('v1')]);
    const { result } = await loaded('latefee', 'selective');

    act(() => result.current.toggle('a'));
    act(() => result.current.toggle('c'));
    act(() => result.current.confirm());

    await waitFor(() => expect(result.current.result).not.toBeNull());
    expect(bodies[0]).toMatchObject({ selectedTargetIds: ['a', 'c'], capabilitiesVersion: 'v1' });
    // Two ticks and Confirm — measured, not a constant — and no pass/fail claim: runs have no
    // product click budget.
    expect(trackInteraction).toHaveBeenCalledWith('latefee-run-confirm', 3, undefined);
  });

  it('counts select-all as one interaction', async () => {
    serve('latefee', [preview('v1')]);
    const { result } = await loaded('latefee', 'selective');

    act(() => result.current.toggleAll());
    expect([...result.current.selected]).toEqual(['a', 'b', 'c']);
    act(() => result.current.confirm());

    await waitFor(() =>
      expect(trackInteraction).toHaveBeenCalledWith('latefee-run-confirm', 2, undefined),
    );
  });

  it('in all-eligible mode confirms every eligible target and nothing else', async () => {
    const { bodies } = serve('rent', [
      preview('v1', [
        row('a'),
        row('b', { alreadyDone: true }),
        row('c', { excludedReason: 'NoActiveLease' }),
      ]),
    ]);
    const { result } = await loaded('rent', 'all-eligible');

    expect([...result.current.selected]).toEqual(['a']);
    act(() => result.current.toggle('a')); // no effect: there is nothing to choose
    expect([...result.current.selected]).toEqual(['a']);
    act(() => result.current.confirm());

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0]!['selectedTargetIds']).toEqual(['a']);
  });

  it('does not report telemetry for a confirm that failed', async () => {
    serve('latefee', [preview('v1')], failure);
    const { result } = await loaded('latefee', 'selective');

    act(() => result.current.toggle('a'));
    act(() => result.current.confirm());

    await waitFor(() => expect(result.current.error).not.toBeNull());
    expect(trackInteraction).not.toHaveBeenCalled();
  });

  it('starts over after a completed run', async () => {
    serve('latefee', [preview('v1')]);
    const { result } = await loaded('latefee', 'selective');

    act(() => result.current.toggle('a'));
    act(() => result.current.confirm());
    await waitFor(() => expect(result.current.result).not.toBeNull());

    act(() => result.current.done());
    expect(result.current.result).toBeNull();
    expect(result.current.selected.size).toBe(0);

    act(() => result.current.toggle('b'));
    act(() => result.current.confirm());
    // The counter restarted: one tick and Confirm, not the first run's interactions as well.
    await waitFor(() =>
      expect(trackInteraction).toHaveBeenLastCalledWith('latefee-run-confirm', 2, undefined),
    );
  });
});

describe('useRunFlow — a capability conflict', () => {
  it('re-previews, clears the selection, and flags the conflict instead of surfacing the raw error', async () => {
    const { previewsServed } = serve('latefee', [preview('v1'), preview('v2')], (n) =>
      n === 1 ? conflict() : HttpResponse.json(RESULT),
    );
    const { result } = await loaded('latefee', 'selective');

    act(() => result.current.toggle('a'));
    act(() => result.current.confirm());

    await waitFor(() => expect(result.current.preview.data?.capabilitiesVersion).toBe('v2'));
    expect(previewsServed()).toBe(2);
    // A tick was a statement about the amounts that were on screen; those amounts are gone.
    expect(result.current.selected.size).toBe(0);
    expect(result.current.conflicted).toBe(true);
    expect(result.current.error).toBeNull();
  });

  it('keeps counting across the re-preview, since the extra ticks were real effort', async () => {
    const { bodies } = serve('latefee', [preview('v1'), preview('v2')], (n) =>
      n === 1 ? conflict() : HttpResponse.json(RESULT),
    );
    const { result } = await loaded('latefee', 'selective');

    act(() => result.current.toggle('a'));
    act(() => result.current.confirm());
    await waitFor(() => expect(result.current.preview.data?.capabilitiesVersion).toBe('v2'));

    act(() => result.current.toggle('a'));
    act(() => result.current.confirm());

    await waitFor(() => expect(result.current.result).not.toBeNull());
    expect(bodies[1]).toMatchObject({ capabilitiesVersion: 'v2' });
    expect(result.current.conflicted).toBe(false);
    // tick, Confirm (conflicted), tick, Confirm.
    expect(trackInteraction).toHaveBeenCalledWith('latefee-run-confirm', 4, undefined);
  });

  it('drops the conflict flag when the period changes', async () => {
    serve('latefee', [preview('v1'), preview('v2')], conflict);
    const { result } = await loaded('latefee', 'selective');

    act(() => result.current.toggle('a'));
    act(() => result.current.confirm());
    await waitFor(() => expect(result.current.conflicted).toBe(true));

    act(() => result.current.setPeriod(2026, 4));
    expect(result.current.conflicted).toBe(false);
  });
});

describe('useRunFlow — a row set is what a tick and an error describe', () => {
  it('clears the selection and the error when any new row set arrives', async () => {
    serve('latefee', [preview('v1'), preview('v1')], failure);
    const { result } = await loaded('latefee', 'selective');

    act(() => result.current.toggle('a'));
    act(() => result.current.confirm());
    await waitFor(() => expect(result.current.error).not.toBeNull());

    // Nothing in the app does this today; a future invalidation must still not leave ticks or an
    // error standing against rows the operator has not seen.
    const before = result.current.preview.dataUpdatedAt;
    await act(() => queryClient.invalidateQueries({ queryKey: ['operations', 'preview'] }));
    await waitFor(() => expect(result.current.preview.dataUpdatedAt).not.toBe(before));

    expect(result.current.selected.size).toBe(0);
    expect(result.current.error).toBeNull();
  });

  it('clears the selection and the error when the period changes, even back to a cached period', async () => {
    // Never stale, so returning to May cannot refetch: only setPeriod's own clearing can pass this.
    queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false, staleTime: Infinity },
        mutations: { retry: false },
      },
    });
    serve('latefee', [preview('v1')], failure);
    const { result } = await loaded('latefee', 'selective');
    const may = result.current.period;

    act(() => result.current.toggle('a'));
    act(() => result.current.confirm());
    await waitFor(() => expect(result.current.error).not.toBeNull());
    const mayUpdatedAt = result.current.preview.dataUpdatedAt;

    act(() => result.current.setPeriod(may.year, may.month - 1 || 12));
    act(() => result.current.setPeriod(may.year, may.month));
    await waitFor(() => expect(result.current.preview.data).toBeDefined());
    expect(result.current.preview.dataUpdatedAt).toBe(mayUpdatedAt); // the same cached row set

    expect(result.current.selected.size).toBe(0);
    expect(result.current.error).toBeNull();
  });
});

describe('useRunFlow — the window before the re-preview lands', () => {
  it('will not confirm the old rows while the preview is refreshing', async () => {
    let releaseSecondPreview!: () => void;
    const secondPreviewGate = new Promise<void>((resolve) => (releaseSecondPreview = resolve));
    const bodies: unknown[] = [];
    let served = 0;
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/operations/runs/latefee/preview', async () => {
        served += 1;
        if (served > 1) await secondPreviewGate;
        return HttpResponse.json(preview(served === 1 ? 'v1' : 'v2'));
      }),
      http.post('/api/operations/runs/latefee/confirm', async ({ request }) => {
        bodies.push(await request.json());
        return conflict();
      }),
    );
    const { result } = await loaded('latefee', 'selective');

    act(() => result.current.toggle('a'));
    act(() => result.current.confirm());
    await waitFor(() => expect(result.current.conflicted).toBe(true));
    await waitFor(() => expect(result.current.isRefreshing).toBe(true));

    // The old rows and ticks are still on screen, but the notice already says they are gone.
    act(() => result.current.confirm());
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(bodies).toHaveLength(1);

    releaseSecondPreview();
    await waitFor(() => expect(result.current.preview.data?.capabilitiesVersion).toBe('v2'));
    expect(result.current.isRefreshing).toBe(false);
  });
});

describe('useRunFlow — other outcomes', () => {
  it('retry clears the ticks and the error', async () => {
    serve('latefee', [preview('v1')], failure);
    const { result } = await loaded('latefee', 'selective');

    act(() => result.current.toggle('a'));
    act(() => result.current.confirm());
    await waitFor(() => expect(result.current.error).not.toBeNull());

    act(() => result.current.retry());
    expect(result.current.selected.size).toBe(0);
    expect(result.current.error).toBeNull();
  });

  it('a since-prior-run conflict is an error, not a re-preview, and keeps the ticks', async () => {
    const { previewsServed } = serve('latefee', [preview('v1')], () =>
      HttpResponse.json(
        {
          code: 'capabilities_changed_since_prior_run',
          detail: 'An earlier run for this period was posted under different features.',
          correlationId: 'p1',
        },
        { status: 409 },
      ),
    );
    const { result } = await loaded('latefee', 'selective');

    act(() => result.current.toggle('a'));
    act(() => result.current.confirm());
    await waitFor(() =>
      expect(result.current.error?.code).toBe('capabilities_changed_since_prior_run'),
    );

    expect(result.current.conflicted).toBe(false);
    expect([...result.current.selected]).toEqual(['a']);
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(previewsServed()).toBe(1);
  });
});
