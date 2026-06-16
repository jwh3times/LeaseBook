import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
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
  render(
    <QueryClientProvider client={queryClient}>
      <LedgerComposer tenantId="t1" onPosted={onPosted} {...props} />
    </QueryClientProvider>,
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
          { code: 'insufficient_liability', detail: 'exceeds held' },
          { status: 409 },
        ),
      ),
    );
    const { onPosted } = renderComposer();

    await userEvent.click(screen.getByRole('button', { name: 'Record payment' }));
    await screen.findByText('Operating Trust');
    await userEvent.type(screen.getByLabelText('Amount'), '1450');
    await userEvent.keyboard('{Enter}');

    expect(await screen.findByRole('alert')).toHaveTextContent('exceeds held');
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
});
