import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import { RentRunScreen } from './RentRunScreen';

vi.mock('@/lib/telemetry', () => ({ trackInteraction: vi.fn() }));

const PREVIEW = {
  runType: 'Rent',
  year: 2026,
  month: 5,
  capabilitiesVersion: 'v1.the-token-the-operator-saw',
  exceptions: [],
  rows: [
    {
      targetId: 'lease-1',
      targetKind: 'Lease',
      label: 'Devon Pryor',
      amount: 1380,
      alreadyDone: false,
      excludedReason: null,
      detail: {},
    },
  ],
};

function renderScreen() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  render(
    <QueryClientProvider client={queryClient}>
      <RentRunScreen />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  document.body.innerHTML = '';
  vi.clearAllMocks();
});

describe('RentRunScreen capability version token', () => {
  it('echoes the preview token back on confirm', async () => {
    let body: Record<string, unknown> | undefined;
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/operations/runs/rent/preview', () => HttpResponse.json(PREVIEW)),
      http.post('/api/operations/runs/rent/confirm', async ({ request }) => {
        body = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json({
          runId: 'run-1',
          runType: 'Rent',
          year: 2026,
          month: 5,
          posted: 1,
          skipped: 0,
          excluded: 0,
          total: 1380,
        });
      }),
    );

    renderScreen();

    const confirmBtn = await screen.findByRole('button', { name: /post 1 charge/i });
    await userEvent.click(confirmBtn);

    await waitFor(() => expect(body).toBeDefined());
    // Verbatim: the client carries the value, it never derives or interprets it.
    expect(body!['capabilitiesVersion']).toBe(PREVIEW.capabilitiesVersion);
  });

  it('surfaces a capabilities_changed 409 and refetches the preview', async () => {
    let previews = 0;
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/operations/runs/rent/preview', () => {
        previews += 1;
        return HttpResponse.json(PREVIEW);
      }),
      http.post('/api/operations/runs/rent/confirm', () =>
        HttpResponse.json(
          {
            code: 'capabilities_changed',
            detail:
              'The features available to this account changed while you were reviewing this run.',
            correlationId: 'abc123',
          },
          { status: 409 },
        ),
      ),
    );

    renderScreen();

    const confirmBtn = await screen.findByRole('button', { name: /post 1 charge/i });
    await waitFor(() => expect(previews).toBe(1));
    await userEvent.click(confirmBtn);

    // The operator is told, in words, that the numbers they approved may have moved.
    expect(
      await screen.findByText(/features available to this account changed/i),
    ).toBeInTheDocument();

    // And the stale preview is refetched, so re-clicking Confirm cannot resubmit the same dead
    // token forever.
    await waitFor(() => expect(previews).toBe(2));
  });
});
