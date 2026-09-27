import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { PaymentPanel } from './PaymentPanel';

function show(staff = false) {
  render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <PaymentPanel staff={staff} />
    </QueryClientProvider>,
  );
}
describe('simulated payments', () => {
  it('preserves the key and amount across an uncertain submission retry', async () => {
    const bodies: unknown[] = [];
    server.use(
      http.get('/api/portal/tenant/payments', () =>
        HttpResponse.json({ enabled: true, items: [] }),
      ),
      http.post('/api/portal/tenant/payments', async ({ request }) => {
        bodies.push(await request.json());
        return HttpResponse.json(
          {
            code: 'internal_error',
            detail: 'Unavailable',
            correlationId: '1234567890abcdef1234567890abcdef',
          },
          { status: 500 },
        );
      }),
    );
    show();
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Amount (USD)'), '1200');
    await user.click(screen.getByRole('button', { name: 'Submit simulated payment' }));
    await user.click(await screen.findByRole('button', { name: 'Retry same payment request' }));
    await waitFor(() => expect(bodies).toHaveLength(2));
    expect(bodies[1]).toEqual(bodies[0]);
    expect(screen.getByLabelText('Amount (USD)')).toHaveAttribute('readonly');
  });
  it('blocks submission on a failed availability read and retains its support reference', async () => {
    server.use(
      http.get('/api/portal/tenant/payments', () =>
        HttpResponse.json(
          {
            code: 'unavailable',
            detail: 'Payments temporarily unavailable.',
            correlationId: '1234567890abcdef1234567890abcdef',
          },
          { status: 503 },
        ),
      ),
    );
    show();
    expect(await screen.findByText('Payments temporarily unavailable.')).toBeVisible();
    expect(screen.getByText('Reference: 1234567890abcdef1234567890abcdef')).toBeVisible();
    expect(
      screen.queryByRole('button', { name: 'Submit simulated payment' }),
    ).not.toBeInTheDocument();
  });
  it('describes a late return without claiming a reversal or offering a force-post action', async () => {
    server.use(
      http.get('/api/payments', () =>
        HttpResponse.json({
          enabled: true,
          items: [
            {
              id: 'operation',
              amount: 1200,
              currency: 'USD',
              status: 'NeedsReview',
              receiptRecorded: true,
              reason: 'return_requires_review',
              createdAt: '2026-09-27T00:00:00Z',
              lastAttemptAt: null,
              canRetry: false,
            },
          ],
        }),
      ),
    );
    show(true);
    expect(
      await screen.findByText('Simulated payment needs review — original receipt remains recorded'),
    ).toBeVisible();
    expect(screen.getByText(/No automatic reversal was made/)).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Retry operation' })).not.toBeInTheDocument();
  });
});
