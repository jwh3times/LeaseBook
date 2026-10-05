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
    quotes(() => ({ fee: 5, charged: 1205 }));
    show();
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Amount (USD)'), '1200');
    await quoteShows(/Total charged/);
    await user.click(screen.getByRole('button', { name: 'Submit simulated payment' }));
    await user.click(await screen.findByRole('button', { name: 'Retry same payment request' }));
    await waitFor(() => expect(bodies).toHaveLength(2));
    expect(bodies[1]).toEqual(bodies[0]);
    expect(bodies[0]).toMatchObject({ amount: '1200', method: 'ach', quotedFee: 5 });
    expect(screen.getByLabelText('Amount (USD)')).toHaveAttribute('readonly');
    expect(screen.getByLabelText('Pay with')).toBeDisabled();
  });

  // The quote is the submit button's description, so it is what a screen reader hears before confirming.
  const quoteShows = (text: RegExp) =>
    waitFor(() =>
      expect(
        screen.getByRole('button', { name: /simulated payment|payment request/i }),
      ).toHaveAccessibleDescription(text),
    );

  // Answers the fee quote and records what was asked. The tenant list is empty unless a test says otherwise.
  function quotes(answer: (method: string, amount: string) => { fee: number; charged: number }) {
    const asked: string[] = [];
    server.use(
      http.get('/api/portal/tenant/payments', () =>
        HttpResponse.json({ enabled: true, items: [] }),
      ),
      http.get('/api/portal/tenant/payments/quote', ({ request }) => {
        const query = new URL(request.url).searchParams;
        const method = query.get('method') ?? '';
        const amount = query.get('amount') ?? '';
        asked.push(`${method} ${amount}`);
        return HttpResponse.json({
          method,
          ledgerAmount: Number(amount),
          ...answer(method, amount),
        });
      }),
    );
    return asked;
  }

  it('shows the fee and the total before the tenant can submit, and requotes when the method changes', async () => {
    const asked = quotes((method) =>
      method === 'card' ? { fee: 15.24, charged: 515.24 } : { fee: 4.03, charged: 504.03 },
    );
    const bodies: unknown[] = [];
    server.use(
      http.post('/api/portal/tenant/payments', async ({ request }) => {
        bodies.push(await request.json());
        return HttpResponse.json({ id: 'operation' }, { status: 202 });
      }),
    );
    show();
    const user = userEvent.setup();
    const submit = await screen.findByRole('button', { name: 'Submit simulated payment' });
    expect(submit).toBeDisabled();
    await user.type(screen.getByLabelText('Amount (USD)'), '500');
    await quoteShows(/Convenience fee: \$4\.03 · Total charged: \$504\.03/);

    await user.selectOptions(screen.getByLabelText('Pay with'), 'card');
    await quoteShows(/Convenience fee: \$15\.24 · Total charged: \$515\.24/);
    expect(asked).toContain('card 500');

    await user.click(submit);
    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0]).toMatchObject({
      amount: '500',
      method: 'card',
      quotedFee: 15.24,
      currency: 'USD',
    });
  });

  it('never asks for a quote it cannot get, and keeps submit blocked for an amount out of range', async () => {
    const asked = quotes(() => ({ fee: 0, charged: 0 }));
    show();
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Amount (USD)'), '10.005');
    expect(screen.getByRole('button', { name: 'Submit simulated payment' })).toBeDisabled();
    expect(screen.queryByText(/Total charged/)).not.toBeInTheDocument();
    expect(asked.filter((quote) => quote.endsWith('10.005'))).toEqual([]);
  });

  it('blocks submission and offers a retry when the fee cannot be calculated', async () => {
    server.use(
      http.get('/api/portal/tenant/payments', () =>
        HttpResponse.json({ enabled: true, items: [] }),
      ),
      http.get('/api/portal/tenant/payments/quote', () =>
        HttpResponse.json(
          {
            code: 'unavailable',
            detail: 'Fees are unavailable right now.',
            correlationId: '1234567890abcdef1234567890abcdef',
          },
          { status: 503 },
        ),
      ),
    );
    show();
    await userEvent.type(await screen.findByLabelText('Amount (USD)'), '500');
    expect(await screen.findByText('Fees are unavailable right now.')).toBeVisible();
    expect(screen.getByText('Reference: 1234567890abcdef1234567890abcdef')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Submit simulated payment' })).toBeDisabled();
  });

  it('shows the new fee and asks the tenant to confirm again when the fee changed since the quote', async () => {
    let fee = 15.24;
    quotes(() => ({ fee, charged: 500 + fee }));
    const bodies: { quotedFee?: number }[] = [];
    server.use(
      http.post('/api/portal/tenant/payments', async ({ request }) => {
        const body = (await request.json()) as { quotedFee?: number };
        bodies.push(body);
        if (body.quotedFee !== 15.74) {
          fee = 15.74;
          return HttpResponse.json(
            {
              code: 'fee_quote_changed',
              detail:
                'The fee for this payment has changed. Review the new total before confirming.',
              correlationId: '1234567890abcdef1234567890abcdef',
            },
            { status: 409 },
          );
        }
        return HttpResponse.json({ id: 'operation' }, { status: 202 });
      }),
    );
    show();
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Amount (USD)'), '500');
    await quoteShows(/Convenience fee: \$15\.24/);
    await user.click(screen.getByRole('button', { name: 'Submit simulated payment' }));

    expect(
      await screen.findByText(
        'The fee for this payment has changed. Review the new total before confirming.',
      ),
    ).toBeVisible();
    expect(
      screen.getByText('Nothing was requested. Check the fee shown above, then submit again.'),
    ).toBeVisible();
    await quoteShows(/Convenience fee: \$15\.74 · Total charged: \$515\.74/);
    // A refused request is not retried as-is: the button submits a new one at the fee now shown.
    await user.click(screen.getByRole('button', { name: 'Submit simulated payment' }));
    await waitFor(() => expect(bodies).toHaveLength(2));
    expect(bodies[1]?.quotedFee).toBe(15.74);
  });

  it('shows the fee and the amount charged on a payment that carried one', async () => {
    server.use(
      http.get('/api/portal/tenant/payments', () =>
        HttpResponse.json({
          enabled: true,
          items: [
            {
              id: 'operation',
              amount: 500,
              quotedFee: 15.24,
              chargedAmount: 515.24,
              method: 'card',
              currency: 'USD',
              status: 'Settled',
              receiptRecorded: true,
              reason: null,
              createdAt: '2026-10-05T00:00:00Z',
              lastAttemptAt: null,
              canRetry: false,
            },
          ],
        }),
      ),
    );
    show();
    const item = await screen.findByRole('listitem');
    expect(item).toHaveTextContent('$500.00');
    expect(item).toHaveTextContent('Plus a $15.24 convenience fee: $515.24 charged');
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
