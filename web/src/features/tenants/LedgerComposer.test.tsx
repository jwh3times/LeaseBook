import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { StrictMode } from 'react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import { trackInteraction } from '@/lib/telemetry';
import { LedgerComposer } from './LedgerComposer';

vi.mock('@/lib/telemetry', () => ({ trackInteraction: vi.fn() }));

const BANKS = [
  {
    id: 'trust1',
    name: 'Operating Trust',
    institution: null,
    mask: null,
    purpose: 'trust',
    isActive: true,
  },
  {
    id: 'dep1',
    name: 'Deposit Trust',
    institution: null,
    mask: null,
    purpose: 'deposit',
    isActive: true,
  },
];

function baseHandlers() {
  return [
    http.get('/api/settings/banks', () => HttpResponse.json(BANKS)),
    http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
  ];
}

function renderComposer(props: Partial<Parameters<typeof LedgerComposer>[0]> = {}) {
  const onPosted = vi.fn();
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  // StrictMode on purpose: the app mounts under it, and it double-invokes effects. The palette's
  // interaction seed (#408) lives next to that effect, and an implementation that counts opens inside
  // it looks correct here and mis-reports in the browser — which is what this wrapper catches.
  render(
    <StrictMode>
      <QueryClientProvider client={queryClient}>
        <LedgerComposer tenantId="t1" onPosted={onPosted} {...props} />
      </QueryClientProvider>
    </StrictMode>,
  );
  return { onPosted };
}

beforeEach(() => {
  document.body.innerHTML = '';
  vi.clearAllMocks();
});

describe('LedgerComposer', () => {
  it('records a payment with defaults in ≤ 3 interactions', async () => {
    let body: Record<string, unknown> | undefined;
    server.use(
      ...baseHandlers(),
      http.post('/api/accounting/tenants/:tenantId/payments', async ({ request }) => {
        body = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json({ entryId: 'pay1' });
      }),
    );
    const { onPosted } = renderComposer();

    await userEvent.click(screen.getByRole('button', { name: 'Record payment' }));
    await screen.findByText('Operating Trust'); // banks loaded → default bank set
    await userEvent.type(screen.getByLabelText('Amount'), '1450');
    await userEvent.keyboard('{Enter}');

    await vi.waitFor(() => expect(onPosted).toHaveBeenCalledWith('pay1'));
    expect(body).toMatchObject({
      tenantId: 't1',
      amount: 1450,
      method: 'ach',
      bankAccountId: 'trust1',
    });
    expect(body?.sourceRef).toEqual(expect.any(String));
    // open (1) + submit (1), no extra choices → met at ≤ 3.
    expect(trackInteraction).toHaveBeenCalledWith('record-payment', 2, true);
  });

  // #408: the palette spends ⌘K + the "Record payment → X" pick before the composer exists, and
  // hands that count over in navigation state. Seeding from it is what makes the budget sample
  // describe the flow the operator actually performed rather than only its last two steps.
  it('continues the palette’s interaction count when the palette auto-opened it', async () => {
    server.use(
      ...baseHandlers(),
      http.post('/api/accounting/tenants/:tenantId/payments', () =>
        HttpResponse.json({ entryId: 'pay1' }),
      ),
    );
    const { onPosted } = renderComposer({ initialMode: 'payment', initialInteractions: 2 });

    await screen.findByText('Operating Trust');
    await userEvent.type(screen.getByLabelText('Amount'), '1450');
    await userEvent.keyboard('{Enter}');

    await vi.waitFor(() => expect(onPosted).toHaveBeenCalledWith('pay1'));
    // ⌘K (1) + pick the action (2) + submit (3) — still inside the ≤ 3 budget.
    expect(trackInteraction).toHaveBeenCalledWith('record-payment', 3, true);
  });

  it('counts only its own interactions for a bookmarked ?compose=payment open', async () => {
    server.use(
      ...baseHandlers(),
      http.post('/api/accounting/tenants/:tenantId/payments', () =>
        HttpResponse.json({ entryId: 'pay1' }),
      ),
    );
    const { onPosted } = renderComposer({ initialMode: 'payment' });

    await screen.findByText('Operating Trust');
    await userEvent.type(screen.getByLabelText('Amount'), '1450');
    await userEvent.keyboard('{Enter}');

    await vi.waitFor(() => expect(onPosted).toHaveBeenCalledWith('pay1'));
    // A refresh or a bookmark carries no navigation state, so the count starts here: open + submit.
    expect(trackInteraction).toHaveBeenCalledWith('record-payment', 2, true);
  });

  it('starts from scratch when the operator reopens it by hand', async () => {
    server.use(
      ...baseHandlers(),
      http.post('/api/accounting/tenants/:tenantId/payments', () =>
        HttpResponse.json({ entryId: 'pay1' }),
      ),
    );
    const { onPosted } = renderComposer({ initialMode: 'payment', initialInteractions: 2 });

    await screen.findByText('Operating Trust');
    await userEvent.type(screen.getByLabelText('Amount'), '{Escape}');
    await userEvent.click(screen.getByRole('button', { name: 'Record payment' }));
    await userEvent.type(await screen.findByLabelText('Amount'), '1450');
    await userEvent.keyboard('{Enter}');

    await vi.waitFor(() => expect(onPosted).toHaveBeenCalledWith('pay1'));
    // The palette's spend belongs to the open it paid for, not to every later open on this page.
    expect(trackInteraction).toHaveBeenCalledWith('record-payment', 2, true);
  });

  it('cancels on Escape', async () => {
    server.use(...baseHandlers());
    renderComposer();

    await userEvent.click(screen.getByRole('button', { name: 'Record payment' }));
    expect(await screen.findByLabelText('Amount')).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText('Amount'), '{Escape}');
    expect(screen.queryByLabelText('Amount')).not.toBeInTheDocument();
  });

  it('adds a late-fee charge with the right kind', async () => {
    let body: Record<string, unknown> | undefined;
    server.use(
      ...baseHandlers(),
      http.post('/api/accounting/tenants/:tenantId/charges', async ({ request }) => {
        body = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json({ entryId: 'chg1' });
      }),
    );
    const { onPosted } = renderComposer();

    await userEvent.click(screen.getByRole('button', { name: 'Add charge' }));
    await userEvent.selectOptions(await screen.findByLabelText('Charge type'), 'Late Fee');
    await userEvent.type(screen.getByLabelText('Amount'), '50');
    await userEvent.keyboard('{Enter}');

    await vi.waitFor(() => expect(onPosted).toHaveBeenCalledWith('chg1'));
    expect(body).toMatchObject({ tenantId: 't1', amount: 50, kind: 'late' });
  });

  it('treats a duplicate source ref as already posted', async () => {
    server.use(
      ...baseHandlers(),
      http.post('/api/accounting/tenants/:tenantId/payments', () =>
        HttpResponse.json(
          { code: 'duplicate_source_ref', existingEntryId: 'existing1', detail: 'dup' },
          { status: 409 },
        ),
      ),
    );
    const { onPosted } = renderComposer();

    await userEvent.click(screen.getByRole('button', { name: 'Record payment' }));
    await screen.findByText('Operating Trust');
    await userEvent.type(screen.getByLabelText('Amount'), '1450');
    await userEvent.keyboard('{Enter}');

    await vi.waitFor(() => expect(onPosted).toHaveBeenCalledWith('existing1'));
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('surfaces a server rejection inline and keeps the composer open', async () => {
    server.use(
      ...baseHandlers(),
      http.post('/api/accounting/tenants/:tenantId/payments', () =>
        HttpResponse.json(
          {
            code: 'insufficient_liability',
            detail: 'Prepayment application of 1200.00 exceeds the 1000.00 held for this tenant.',
          },
          { status: 409 },
        ),
      ),
    );
    const { onPosted } = renderComposer();

    await userEvent.click(screen.getByRole('button', { name: 'Record payment' }));
    await screen.findByText('Operating Trust');
    await userEvent.type(screen.getByLabelText('Amount'), '1450');
    await userEvent.keyboard('{Enter}');

    expect(await screen.findByRole('alert')).toHaveTextContent(/exceeds the 1000.00 held/i);
    expect(onPosted).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Amount')).toBeInTheDocument();
  });

  it('surfaces the M4 account-lock (409) inline and keeps the composer open', async () => {
    server.use(
      ...baseHandlers(),
      http.post('/api/accounting/tenants/:tenantId/payments', () =>
        HttpResponse.json(
          {
            code: 'account_period_locked',
            detail:
              'This bank account is reconciled and locked for 2026-02; post into the open month.',
          },
          { status: 409 },
        ),
      ),
    );
    const { onPosted } = renderComposer();

    await userEvent.click(screen.getByRole('button', { name: 'Record payment' }));
    await screen.findByText('Operating Trust');
    await userEvent.type(screen.getByLabelText('Amount'), '1450');
    await userEvent.keyboard('{Enter}');

    expect(await screen.findByRole('alert')).toHaveTextContent(/reconciled and locked/i);
    expect(onPosted).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Amount')).toBeInTheDocument();
  });

  it('auto-opens in payment mode from the palette flag', async () => {
    server.use(...baseHandlers());
    renderComposer({ initialMode: 'payment' });
    expect(
      await screen.findByText('Record payment', { selector: '.pf-composer-tag' }),
    ).toBeInTheDocument();
  });

  it('excludes inactive bank accounts from the bank picker', async () => {
    // The composer calls useBankAccounts(true) → activeOnly=true is sent.
    // MSW returns only the active bank (simulating the server filter).
    server.use(
      http.get('/api/settings/banks', ({ request }) => {
        const url = new URL(request.url);
        const activeOnly = url.searchParams.get('activeOnly');
        const activeBanks = BANKS.filter((b) => (activeOnly === 'true' ? b.isActive : true));
        return HttpResponse.json(activeBanks);
      }),
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
    );

    // Render with an inactive bank that should not appear
    const inactiveBank = {
      id: 'inactive1',
      name: 'Inactive Trust',
      institution: null,
      mask: null,
      purpose: 'trust',
      isActive: false,
    };

    // Override BANKS to include the inactive one for this test
    server.use(
      http.get('/api/settings/banks', ({ request }) => {
        const url = new URL(request.url);
        const activeOnly = url.searchParams.get('activeOnly');
        const allBanks = [...BANKS, inactiveBank];
        return HttpResponse.json(activeOnly === 'true' ? BANKS : allBanks);
      }),
    );

    renderComposer({ initialMode: 'payment' });
    await screen.findByText('Record payment', { selector: '.pf-composer-tag' });
    // Wait for bank options to load
    expect(await screen.findByText('Operating Trust')).toBeInTheDocument();
    // The inactive bank must not appear in the picker
    expect(screen.queryByText('Inactive Trust')).not.toBeInTheDocument();
  });
});

describe('LedgerComposer when the bank list cannot be read', () => {
  it('reports the read failure instead of asking for a selection that cannot be made', async () => {
    let posted = false;
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/settings/banks', () => new HttpResponse(null, { status: 503 })),
      http.post('/api/accounting/tenants/:tenantId/payments', () => {
        posted = true;
        return HttpResponse.json({ entryId: 'e1' });
      }),
    );
    renderComposer({ initialMode: 'payment' });

    expect(await screen.findByRole('alert')).toBeInTheDocument();

    // "Select a bank account." is advice the operator cannot act on: the selector has nothing in
    // it because the read failed, not because they skipped a field.
    expect(screen.queryByText(/select a bank account/i)).toBeNull();

    await userEvent.type(screen.getByLabelText('Amount'), '100');
    await userEvent.keyboard('{Enter}');
    expect(posted).toBe(false);
  });

  it('recovers and posts once the bank list loads', async () => {
    let attempt = 0;
    let posted = false;
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/settings/banks', () => {
        attempt += 1;
        return attempt === 1 ? new HttpResponse(null, { status: 503 }) : HttpResponse.json(BANKS);
      }),
      http.post('/api/accounting/tenants/:tenantId/payments', () => {
        posted = true;
        return HttpResponse.json({ entryId: 'e1' });
      }),
    );
    renderComposer({ initialMode: 'payment' });

    await userEvent.click(await screen.findByRole('button', { name: /retry/i }));
    await screen.findByLabelText('Bank account');

    await userEvent.type(screen.getByLabelText('Amount'), '100');
    await userEvent.keyboard('{Enter}');

    await vi.waitFor(() => expect(posted).toBe(true));
  });
});

// #468 (ADR-047): the description prints on the owner statement, and the note never leaves staff
// surfaces. The audience is part of each field's accessible description, not just its colour.
describe('LedgerComposer description and internal note', () => {
  it('labels the description as owner-facing and the note as staff-only', async () => {
    server.use(...baseHandlers());
    renderComposer({ initialMode: 'payment' });

    const description = await screen.findByLabelText('Statement description');
    expect(description).toHaveAccessibleDescription('Owner sees this');
    const note = screen.getByLabelText('Internal note');
    expect(note).toHaveAccessibleDescription('Staff only');
    // "Memo" read as private while it printed on every owner statement.
    expect(screen.queryByLabelText(/memo/i)).toBeNull();
  });

  it('posts the description and the note as separate fields', async () => {
    let body: Record<string, unknown> | undefined;
    server.use(
      ...baseHandlers(),
      http.post('/api/accounting/tenants/:tenantId/payments', async ({ request }) => {
        body = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json({ entryId: 'pay1' });
      }),
    );
    const { onPosted } = renderComposer({ initialMode: 'payment' });

    await screen.findByText('Operating Trust');
    await userEvent.type(screen.getByLabelText('Amount'), '1450');
    await userEvent.type(screen.getByLabelText('Statement description'), ' June rent ');
    await userEvent.type(screen.getByLabelText('Internal note'), 'Paid at the office');
    await userEvent.keyboard('{Enter}'); // Enter posts from the note field too

    await vi.waitFor(() => expect(onPosted).toHaveBeenCalledWith('pay1'));
    expect(body).toMatchObject({ description: 'June rent', internalNote: 'Paid at the office' });
    expect(body).not.toHaveProperty('memo');
  });

  it('sends null for blank fields rather than empty strings', async () => {
    let body: Record<string, unknown> | undefined;
    server.use(
      ...baseHandlers(),
      http.post('/api/accounting/tenants/:tenantId/charges', async ({ request }) => {
        body = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json({ entryId: 'c1' });
      }),
    );
    const { onPosted } = renderComposer({ initialMode: 'charge' });

    await userEvent.type(await screen.findByLabelText('Amount'), '25');
    await userEvent.type(screen.getByLabelText('Internal note'), '   ');
    await userEvent.keyboard('{Enter}');

    await vi.waitFor(() => expect(onPosted).toHaveBeenCalledWith('c1'));
    expect(body).toMatchObject({ description: null, internalNote: null });
  });

  it('presents a credit’s reason as owner-facing and posts the note beside it', async () => {
    let body: Record<string, unknown> | undefined;
    server.use(
      ...baseHandlers(),
      http.post('/api/accounting/tenants/:tenantId/credits', async ({ request }) => {
        body = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json({ entryId: 'cr1' });
      }),
    );
    const { onPosted } = renderComposer({ initialMode: 'charge' });

    await userEvent.selectOptions(await screen.findByLabelText('Charge type'), 'Credit');
    const reason = screen.getByLabelText('Statement reason');
    expect(reason).toHaveAccessibleDescription('Owner sees this');
    await userEvent.type(screen.getByLabelText('Amount'), '40');
    await userEvent.type(reason, 'Goodwill credit');
    await userEvent.type(screen.getByLabelText('Internal note'), 'Approved by the owner by phone');
    await userEvent.keyboard('{Enter}');

    await vi.waitFor(() => expect(onPosted).toHaveBeenCalledWith('cr1'));
    expect(body).toMatchObject({
      reason: 'Goodwill credit',
      internalNote: 'Approved by the owner by phone',
    });
  });
});
