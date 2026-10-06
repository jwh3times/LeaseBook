import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { OrgSettings } from '@/lib/settings';
import { server } from '@/test/mocks/server';
import { PaymentFeeSettings } from './PaymentFeeSettings';

const settings = {
  cardFeeRateBps: 290,
  cardFeeFixed: 0.3,
  cardFeeCap: null,
  achFeeRateBps: 80,
  achFeeFixed: 0,
  achFeeCap: 5,
} as OrgSettings;

function show(role: string, enabled = true) {
  server.use(
    http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
    http.get('/api/auth/me', () => HttpResponse.json({ email: 'a@b.test', role })),
    http.get('/api/payments', () => HttpResponse.json({ enabled, items: [] })),
  );
  render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <PaymentFeeSettings initial={settings} />
    </QueryClientProvider>,
  );
}

describe('online payment fee settings', () => {
  it('is absent where there are no online payments', async () => {
    let asked = false;
    server.use(
      http.get('/api/auth/me', () => HttpResponse.json({ email: 'a@b.test', role: 'PMAdmin' })),
      http.get('/api/payments', () => {
        asked = true;
        return HttpResponse.json({ enabled: false, items: [] });
      }),
    );
    render(
      <QueryClientProvider
        client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
      >
        <PaymentFeeSettings initial={settings} />
      </QueryClientProvider>,
    );
    await waitFor(() => expect(asked).toBe(true));
    expect(screen.queryByText('Online payment fees')).not.toBeInTheDocument();
  });

  it('shows each rule as a percentage, a fixed amount and a cap, and saves both together', async () => {
    let sent: unknown;
    server.use(
      http.put('/api/settings/payment-fees', async ({ request }) => {
        sent = await request.json();
        return HttpResponse.json({ ...settings, cardFeeRateBps: 310 });
      }),
    );
    show('PMAdmin');
    const card = await screen.findByRole('group', { name: 'Card' });
    expect(card).toBeEnabled();
    const rate = screen.getAllByLabelText('Rate (%)');
    expect(rate.map((input) => (input as HTMLInputElement).value)).toEqual(['2.9', '0.8']);
    expect(
      screen.getAllByLabelText('Fixed amount ($)').map((i) => (i as HTMLInputElement).value),
    ).toEqual(['0.30', '0.00']);
    const caps = screen.getAllByLabelText('Cap ($)');
    expect(caps.map((i) => (i as HTMLInputElement).value)).toEqual(['', '5.00']);

    await userEvent.clear(rate[0]!);
    await userEvent.type(rate[0]!, '3.1');
    await userEvent.clear(caps[1]!);
    await userEvent.type(caps[1]!, '{Enter}');

    expect(await screen.findByText('Saved')).toBeVisible();
    expect(sent).toEqual({
      cardFeeRateBps: 310,
      cardFeeFixed: 0.3,
      cardFeeCap: null,
      achFeeRateBps: 80,
      achFeeFixed: 0,
      achFeeCap: null,
    });
  });

  it('shows a refusal from the server and does not claim to have saved', async () => {
    server.use(
      http.put('/api/settings/payment-fees', () =>
        HttpResponse.json(
          { title: 'validation_failed', detail: 'A cap cannot be below the fixed amount.' },
          { status: 400 },
        ),
      ),
    );
    show('PMAdmin');
    await userEvent.click(await screen.findByRole('button', { name: 'Save payment fees' }));
    expect(await screen.findByText('A cap cannot be below the fixed amount.')).toBeVisible();
    expect(screen.queryByText('Saved')).not.toBeInTheDocument();
  });

  it('lets staff who are not administrators read the rules but not change them', async () => {
    show('PMStaff');
    expect(await screen.findByRole('group', { name: 'Card' })).toBeDisabled();
    expect(screen.getByText('Only an administrator can change these fees.')).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Save payment fees' })).not.toBeInTheDocument();
  });
});
