/**
 * The two bulk-run screens without a test file of their own. Both post money from a preview, so a
 * preview that failed to read must say so with its support reference (ADR-025) rather than as a
 * blank "Couldn't load preview" the operator can only respond to by reloading the page.
 */
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import { DisbursementRunScreen } from './DisbursementRunScreen';
import { LateFeeRunScreen } from './LateFeeRunScreen';

vi.mock('@/lib/telemetry', () => ({ trackInteraction: vi.fn() }));

function previewBody(label: string, runType: string) {
  return {
    runType,
    year: 2026,
    month: 5,
    capabilitiesVersion: 'v1',
    exceptions: [],
    rows: [
      {
        targetId: 'target-1',
        targetKind: 'Lease',
        label,
        amount: 75,
        alreadyDone: false,
        excludedReason: null,
        detail: {},
      },
    ],
  };
}

function renderScreen(element: React.ReactElement) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  render(<QueryClientProvider client={queryClient}>{element}</QueryClientProvider>);
}

beforeEach(() => {
  document.body.innerHTML = '';
  vi.clearAllMocks();
});

describe.each([
  {
    name: 'LateFeeRunScreen',
    path: '/api/operations/runs/latefee/preview',
    element: <LateFeeRunScreen />,
    runType: 'LateFee',
    label: 'Aisha Bello',
    detail: 'The late-fee policy is unavailable.',
    reference: 'ab12ab12ab12ab12ab12ab12ab12ab12',
  },
  {
    name: 'DisbursementRunScreen',
    path: '/api/operations/runs/disbursement/preview',
    element: <DisbursementRunScreen />,
    runType: 'Disbursement',
    label: 'Hargrove Family Trust',
    detail: 'Owner balances are unavailable.',
    reference: 'cd34cd34cd34cd34cd34cd34cd34cd34',
  },
])('$name when the preview cannot be read', (run) => {
  it('carries the support reference and retries in place', async () => {
    let attempt = 0;
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get(run.path, () => {
        attempt += 1;
        return attempt === 1
          ? HttpResponse.json({ detail: run.detail, correlationId: run.reference }, { status: 503 })
          : HttpResponse.json(previewBody(run.label, run.runType));
      }),
    );
    renderScreen(run.element);

    expect(await screen.findByText("Couldn't load preview")).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent(run.detail);
    expect(screen.getByText(`Reference: ${run.reference}`)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));

    expect(await screen.findByText(run.label)).toBeInTheDocument();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
