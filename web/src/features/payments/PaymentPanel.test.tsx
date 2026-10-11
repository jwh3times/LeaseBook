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
  it('says why a payment with no outcome after seven days waits for a person', async () => {
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
              receiptRecorded: false,
              reason: 'outcome_overdue',
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
      await screen.findByText('Simulated payment needs review — no receipt recorded'),
    ).toBeVisible();
    expect(
      screen.getByText(
        'The processor reported no outcome for this payment within seven days. Nothing was posted. Check the payment with the processor, then close the review.',
      ),
    ).toBeVisible();
    // Not the fallback for a reason the page does not know, and nothing to send again.
    expect(screen.queryByText(/Contact the fixture operator/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Retry operation' })).not.toBeInTheDocument();
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

  const collected = {
    id: 'operation',
    amount: 500,
    currency: 'USD',
    status: 'Processing',
    receiptRecorded: false,
    reason: null,
    createdAt: '2026-10-05T00:00:00Z',
    paidAt: '2026-10-05T14:30:00Z',
    lastAttemptAt: null,
    canRetry: false,
  };

  it('tells a tenant what is in transit and that their balance still includes it', async () => {
    server.use(
      http.get('/api/portal/tenant/payments', () =>
        HttpResponse.json({ enabled: true, items: [collected], fundsInTransit: 500 }),
      ),
    );
    show();
    const notice = await screen.findByRole('status');
    expect(notice).toHaveTextContent('In transit: $500.00.');
    expect(notice).toHaveTextContent('Your balance still includes it until it arrives.');
    expect(
      screen.getByText('Paid on 2026-10-05 — on its way to the bank, not yet on your ledger'),
    ).toBeVisible();
  });

  it('tells staff the organization total in transit and says nothing when there is none', async () => {
    let inTransit = 850;
    server.use(
      http.get('/api/payments', () =>
        HttpResponse.json({ enabled: true, items: [], fundsInTransit: inTransit }),
      ),
    );
    show(true);
    const notice = await screen.findByRole('status');
    expect(notice).toHaveTextContent('In transit: $850.00.');
    expect(notice).toHaveTextContent('Tenant balances and the bank book do not include it.');

    inTransit = 0;
    await waitFor(() => expect(screen.queryByRole('status')).not.toBeInTheDocument(), {
      timeout: 4000,
    });
  });
});

describe('payments by mode', () => {
  const item = (extra: object = {}) => ({
    id: 'operation',
    amount: 1200,
    currency: 'USD',
    status: 'Settled',
    receiptRecorded: true,
    reason: null,
    createdAt: '2026-09-27T00:00:00Z',
    lastAttemptAt: null,
    canRetry: false,
    ...extra,
  });
  const tenantSees = (mode: string | null | undefined, ...items: object[]) =>
    server.use(
      http.get('/api/portal/tenant/payments', () =>
        HttpResponse.json({ enabled: true, items, mode }),
      ),
    );
  const staffSee = (mode: string | null | undefined, ...items: object[]) =>
    server.use(
      http.get('/api/payments', () => HttpResponse.json({ enabled: true, items, mode })),
      http.get('/api/payments/settlements', () => HttpResponse.json({ items: [] })),
    );

  it('calls a sandbox payment a test payment everywhere a tenant reads', async () => {
    tenantSees('StripeSandbox');
    show();
    expect(await screen.findByRole('heading', { name: 'Test payments' })).toBeVisible();
    expect(screen.getByText('Test payment — no real money moves')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Submit test payment' })).toBeDisabled();
    expect(screen.getByText('No test payments yet.')).toBeVisible();
    expect(document.body).not.toHaveTextContent(/simulat/i);
  });

  it.each([
    ['Requested', {}, 'Test payment requested'],
    ['Processing', {}, 'Test payment processing — not yet on your ledger'],
    ['Failed', { receiptRecorded: false }, 'Test payment failed — no payment recorded'],
    ['Settled', {}, 'Test payment recorded'],
    ['Returned', {}, 'Test payment returned by the bank — the receipt was reversed'],
    ['ReviewClosed', {}, 'Test payment review closed — original receipt remains recorded'],
    [
      'ReviewClosed',
      { receiptRecorded: false },
      'Test payment review closed — no receipt recorded',
    ],
    ['NeedsReview', {}, 'Test payment needs review — original receipt remains recorded'],
    ['NeedsReview', { receiptRecorded: false }, 'Test payment needs review — no receipt recorded'],
  ])('words a sandbox payment that is %s %o', async (status, extra, text) => {
    tenantSees('StripeSandbox', item({ status, ...extra }));
    show();
    expect(await screen.findByText(text, { exact: true })).toBeVisible();
    expect(document.body).not.toHaveTextContent(/simulat/i);
  });

  it('calls a sandbox payment a test payment on the staff screen, down to a failed collection', async () => {
    staffSee(
      'StripeSandbox',
      item({ status: 'Failed', receiptRecorded: false, reason: 'collection_failed' }),
    );
    show(true);
    expect(await screen.findByRole('heading', { name: 'Test payments' })).toBeVisible();
    expect(screen.getByText('Test payment — no real money moves')).toBeVisible();
    expect(screen.getByText('Test payment failed — no payment recorded')).toBeVisible();
    expect(
      screen.getByText('Collection failed. The tenant may submit a new test payment.'),
    ).toBeVisible();
    // A sandbox payout is first heard of from the processor, not from a bank.
    expect(
      await screen.findByText(
        'No payouts yet. A payout appears here when the processor reports it.',
      ),
    ).toBeVisible();
    expect(document.body).not.toHaveTextContent(/simulat/i);
  });

  it('words a sandbox submission that could not be confirmed, when the server explains nothing', async () => {
    tenantSees('StripeSandbox');
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/portal/tenant/payments/quote', () =>
        HttpResponse.json({ method: 'ach', ledgerAmount: 50, fee: 0, charged: 50 }),
      ),
      http.post('/api/portal/tenant/payments', () => HttpResponse.json({}, { status: 502 })),
    );
    show();
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Amount (USD)'), '50');
    const submit = screen.getByRole('button', { name: 'Submit test payment' });
    await waitFor(() => expect(submit).toBeEnabled());
    await user.click(submit);
    expect(await screen.findByText('Unable to confirm the test payment request.')).toBeVisible();
  });

  it('keeps the sandbox wording when a later read of the list fails without explanation', async () => {
    let fail = false;
    server.use(
      http.get('/api/portal/tenant/payments', () =>
        fail
          ? HttpResponse.json({}, { status: 502 })
          : HttpResponse.json({ enabled: true, items: [], mode: 'StripeSandbox' }),
      ),
    );
    show();
    expect(await screen.findByRole('heading', { name: 'Test payments' })).toBeVisible();
    fail = true;
    expect(
      await screen.findByText('Couldn’t load test payments', {}, { timeout: 6000 }),
    ).toBeVisible();
    expect(screen.getByText('Unable to load test payments.')).toBeVisible();
    expect(document.body).not.toHaveTextContent(/simulat/i);
  }, 10000);

  it.each([['Simulation'], [null], [undefined], ['SomethingNew']])(
    'keeps the simulation wording for mode %s',
    async (mode) => {
      staffSee(
        mode,
        item({ status: 'Failed', receiptRecorded: false, reason: 'collection_failed' }),
      );
      show(true);
      expect(await screen.findByRole('heading', { name: 'Simulated payments' })).toBeVisible();
      expect(screen.getByText('Simulation — no real money moves')).toBeVisible();
      expect(screen.getByText('Simulated payment failed — no payment recorded')).toBeVisible();
      expect(
        screen.getByText('Collection failed. The tenant may submit a new simulated payment.'),
      ).toBeVisible();
      expect(
        await screen.findByText('No payouts yet. A payout appears here when the bank reports it.'),
      ).toBeVisible();
      expect(document.body).not.toHaveTextContent(/test payment/i);
    },
  );

  it('tells a tenant an overdue payment is being checked, and never shows the code or the staff text', async () => {
    tenantSees(
      'StripeSandbox',
      item({ status: 'NeedsReview', receiptRecorded: false, reason: 'outcome_overdue' }),
    );
    show();
    expect(
      await screen.findByText(
        'This payment is waiting for your property manager to check it. Nothing has been recorded on your ledger.',
      ),
    ).toBeVisible();
    expect(document.body).not.toHaveTextContent('outcome_overdue');
    expect(document.body).not.toHaveTextContent(/Reason:/);
    expect(document.body).not.toHaveTextContent(/processor reported no outcome/);
  });

  it('does not tell a tenant nothing was recorded when a receipt was, whatever the reason', async () => {
    tenantSees('StripeSandbox', item({ status: 'NeedsReview', reason: 'return_requires_review' }));
    show();
    expect(
      await screen.findByText('This payment is waiting for your property manager to check it.', {
        exact: true,
      }),
    ).toBeVisible();
    expect(document.body).not.toHaveTextContent('return_requires_review');
    expect(document.body).not.toHaveTextContent(/Nothing has been recorded/);
    expect(document.body).not.toHaveTextContent(/No automatic reversal/);
  });

  it('says nothing more to a tenant about a payment that is not in review', async () => {
    tenantSees('StripeSandbox', item());
    show();
    expect(await screen.findByText('Test payment recorded')).toBeVisible();
    expect(document.body).not.toHaveTextContent(/waiting for your property manager/);
  });

  it('leaves the simulation tenant screen as it was for a payment in review', async () => {
    tenantSees(
      'Simulation',
      item({ status: 'NeedsReview', reason: 'outcome_overdue', receiptRecorded: false }),
    );
    show();
    expect(
      await screen.findByText('Simulated payment needs review — no receipt recorded'),
    ).toBeVisible();
    expect(document.body).not.toHaveTextContent(/waiting for your property manager/);
    expect(document.body).not.toHaveTextContent('outcome_overdue');
  });

  const admin = () =>
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
    );

  it('says a return can be posted only when the receipt posted and the server offers the action', async () => {
    admin();
    staffSee(
      'StripeSandbox',
      item({
        status: 'NeedsReview',
        reason: 'return_requires_review',
        canPostReturn: true,
        canCloseReview: true,
      }),
    );
    show(true);
    expect(
      await screen.findByText(
        'A return, refund or dispute arrived after the receipt posted. No automatic reversal was made. An administrator can post a full return, or close the review after correcting the ledger.',
      ),
    ).toBeVisible();
    expect(await screen.findByRole('button', { name: 'Post return' })).toBeVisible();
  });

  it('does not offer or describe posting a return the server will not post', async () => {
    admin();
    staffSee(
      'StripeSandbox',
      item({
        status: 'NeedsReview',
        reason: 'return_requires_review',
        canPostReturn: false,
        canCloseReview: true,
      }),
    );
    show(true);
    expect(
      await screen.findByText(
        'A return, refund or dispute was reported for this payment, and its receipt is on the ledger. No automatic reversal was made. The evidence held so far cannot be posted as a return. Review the evidence and the ledger, then close the review.',
      ),
    ).toBeVisible();
    expect(await screen.findByRole('button', { name: 'Close review…' })).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Post return' })).not.toBeInTheDocument();
    expect(document.body).not.toHaveTextContent(/can post a full return/);
  });

  it('does not speak of a posted receipt or a return to post when no receipt posted', async () => {
    admin();
    staffSee(
      'StripeSandbox',
      item({
        status: 'NeedsReview',
        receiptRecorded: false,
        reason: 'return_requires_review',
        canPostReturn: false,
        canCloseReview: true,
      }),
    );
    show(true);
    expect(
      await screen.findByText(
        'A return, refund or dispute was reported for this payment. No receipt has posted for it, so there is nothing to reverse and nothing was posted. Review the evidence, then close the review.',
      ),
    ).toBeVisible();
    expect(await screen.findByRole('button', { name: 'Close review…' })).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Post return' })).not.toBeInTheDocument();
    expect(document.body).not.toHaveTextContent(/after the receipt posted/);
  });
});
