import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import type { ComponentType } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import { DisbursementRunScreen } from './DisbursementRunScreen';
import { LateFeeRunScreen } from './LateFeeRunScreen';

vi.mock('@/lib/telemetry', () => ({ trackInteraction: vi.fn() }));

/**
 * The selective screens are wired to `useRunFlow` (whose state machine is tested on its own): a
 * capability conflict re-previews, the tick the operator made against the old amounts is gone, and
 * the screen says why.
 */
const cases: {
  name: string;
  type: string;
  Screen: ComponentType;
  confirm: RegExp;
  kind: string;
}[] = [
  {
    name: 'LateFeeRunScreen',
    type: 'latefee',
    Screen: LateFeeRunScreen,
    confirm: /charge 1 lease/i,
    kind: 'Lease',
  },
  {
    name: 'DisbursementRunScreen',
    type: 'disbursement',
    Screen: DisbursementRunScreen,
    confirm: /disburse 1 owner/i,
    kind: 'Owner',
  },
];

beforeEach(() => {
  document.body.innerHTML = '';
  vi.clearAllMocks();
});

describe.each(cases)('$name after a capability conflict', ({ type, Screen, confirm, kind }) => {
  it('clears the tick against the old amounts and explains why', async () => {
    let previews = 0;
    const preview = (token: string, amount: number) => ({
      runType: type,
      year: 2026,
      month: 5,
      capabilitiesVersion: token,
      exceptions: [],
      rows: [
        {
          targetId: 't-1',
          targetKind: kind,
          label: 'Ridgeline Investments',
          amount,
          alreadyDone: false,
          excludedReason: null,
          detail: { equity: '1000', fee: '70', reserve: '0' },
        },
      ],
    });
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get(`/api/operations/runs/${type}/preview`, () => {
        previews += 1;
        return HttpResponse.json(previews === 1 ? preview('v1', 930) : preview('v2', 900));
      }),
      http.get(`/api/operations/runs/${type}/preview/issued-coverage`, () =>
        HttpResponse.json({ rows: [] }),
      ),
      http.post(`/api/operations/runs/${type}/confirm`, () =>
        HttpResponse.json(
          { code: 'capabilities_changed', detail: 'The features changed.', correlationId: 'c1' },
          { status: 409 },
        ),
      ),
    );

    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    render(
      <QueryClientProvider client={queryClient}>
        <Screen />
      </QueryClientProvider>,
    );

    await userEvent.click(
      await screen.findByRole('checkbox', { name: 'Select Ridgeline Investments' }),
    );
    await userEvent.click(screen.getByRole('button', { name: confirm }));

    expect(await screen.findByText(/amounts were recalculated/i)).toBeInTheDocument();
    // The re-preview carries different amounts; wait for them, then the tick must be gone.
    expect(await screen.findByText(/900/)).toBeInTheDocument();
    expect(previews).toBe(2);
    expect(
      screen.getByRole('checkbox', { name: 'Select Ridgeline Investments' }),
    ).not.toBeChecked();
    // The server's own wording is replaced, not shown alongside.
    expect(screen.queryByText(/the features changed/i)).toBeNull();
    expect(screen.queryByRole('button', { name: confirm })).toBeNull();
  });
});
