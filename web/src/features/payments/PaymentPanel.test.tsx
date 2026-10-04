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
    // Signed out in this test: the review actions belong to an administrator.
    expect(screen.queryByRole('button', { name: 'Post return' })).not.toBeInTheDocument();
  });

  const inReview = {
    id: 'operation',
    amount: 600,
    currency: 'USD',
    status: 'NeedsReview',
    receiptRecorded: true,
    reason: 'return_requires_review',
    createdAt: '2026-09-27T00:00:00Z',
    lastAttemptAt: null,
    canRetry: false,
    canPostReturn: true,
    canCloseReview: true,
    receiptEntryId: 'receipt-entry',
  };
  function asAdmin(...items: object[]) {
    let current = items;
    server.use(
      http.get('/api/auth/me', () =>
        HttpResponse.json({
          userId: 'user',
          name: 'Admin',
          email: 'admin@example.com',
          role: 'PMAdmin',
          orgId: 'org',
          orgName: 'Fixture',
        }),
      ),
      http.get('/api/payments', () => HttpResponse.json({ enabled: true, items: current })),
    );
    return (...next: object[]) => {
      current = next;
    };
  }

  it('posts a return and shows the receipt and reversal references', async () => {
    const setItems = asAdmin(inReview);
    const returned = {
      ...inReview,
      status: 'Returned',
      reason: null,
      canPostReturn: false,
      canCloseReview: false,
      returnEntryId: 'reversal-entry',
    };
    server.use(
      http.post('/api/payments/operation/return', () => {
        setItems(returned);
        return HttpResponse.json(returned);
      }),
    );
    show(true);
    await userEvent.click(await screen.findByRole('button', { name: 'Post return' }));
    expect(
      await screen.findByText('Simulated payment returned by the bank — the receipt was reversed'),
    ).toBeVisible();
    expect(screen.getByText('Receipt entry: receipt-entry')).toBeVisible();
    expect(screen.getByText('Reversal entry: reversal-entry')).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Post return' })).not.toBeInTheDocument();
  });

  it('shows why a return was refused, with its support reference, and keeps the close action', async () => {
    const setItems = asAdmin(inReview);
    server.use(
      http.post('/api/payments/operation/return', () => {
        setItems({ ...inReview, reason: 'return_owner_funds_disbursed' });
        return HttpResponse.json(
          {
            code: 'return_owner_funds_disbursed',
            detail: 'Funds from this payment have since left the owner’s balance.',
            correlationId: '1234567890abcdef1234567890abcdef',
          },
          { status: 409 },
        );
      }),
    );
    show(true);
    await userEvent.click(await screen.findByRole('button', { name: 'Post return' }));
    expect(
      await screen.findByText('Funds from this payment have since left the owner’s balance.'),
    ).toBeVisible();
    expect(screen.getByText('Reference: 1234567890abcdef1234567890abcdef')).toBeVisible();
    expect(await screen.findByText('Reason: return_owner_funds_disbursed')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Close review…' })).toBeVisible();
  });

  it('closes a review only with a note and then shows the note', async () => {
    const setItems = asAdmin(inReview);
    const bodies: unknown[] = [];
    server.use(
      http.post('/api/payments/operation/close-review', async ({ request }) => {
        bodies.push(await request.json());
        const closed = {
          ...inReview,
          status: 'ReviewClosed',
          canPostReturn: false,
          canCloseReview: false,
          reviewNote: 'Corrected by adjustment.',
        };
        setItems(closed);
        return HttpResponse.json(closed);
      }),
    );
    show(true);
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Close review…' }));
    const submit = screen.getByRole('button', { name: 'Close review' });
    expect(submit).toBeDisabled();
    await user.type(
      screen.getByLabelText('How was this resolved? (staff only)'),
      'Corrected by adjustment.',
    );
    await user.click(submit);
    expect(
      await screen.findByText('Review closed by staff: Corrected by adjustment.'),
    ).toBeVisible();
    expect(bodies).toEqual([{ note: 'Corrected by adjustment.' }]);
    expect(
      screen.getByText('Simulated payment review closed — original receipt remains recorded'),
    ).toBeVisible();
  });
});
