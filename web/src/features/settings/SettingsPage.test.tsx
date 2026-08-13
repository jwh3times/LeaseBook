import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { SettingsPage } from './SettingsPage';

const ORG = {
  accountingBasis: 'cash',
  moneyNegativeDisplay: 'minus',
  legalName: 'Tarheel Property Group',
  address: null,
  city: 'Asheville',
  state: 'NC',
  zip: null,
  phone: null,
  logoBlobRef: null,
  rentDueDay: 1,
  lateFeeGraceDays: 5,
  lateFeeKind: 'flat',
  lateFeeAmount: 50,
  lateFeeRateBps: 500,
};

const ACTIVE_BANK = {
  id: 'b1',
  name: 'Operating Trust',
  institution: 'First Citizens',
  mask: '4021',
  purpose: 'trust',
  isActive: true,
};

function renderSettings() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <SettingsPage />
    </QueryClientProvider>,
  );
}

describe('SettingsPage', () => {
  it('loads the org profile and persists a basis change', async () => {
    let saved: { accountingBasis?: string; moneyNegativeDisplay?: string } | null = null;
    server.use(
      http.get('/api/settings/org', () => HttpResponse.json(ORG)),
      http.get('/api/settings/banks', () => HttpResponse.json([])),
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.put('/api/settings/org', async ({ request }) => {
        saved = (await request.json()) as { accountingBasis: string; moneyNegativeDisplay: string };
        return HttpResponse.json({ ...ORG, ...saved });
      }),
    );

    renderSettings();
    expect(await screen.findByDisplayValue('Tarheel Property Group')).toBeInTheDocument();

    await userEvent.selectOptions(screen.getByLabelText('Accounting basis'), 'accrual');
    await userEvent.selectOptions(screen.getByLabelText('Negative amounts'), 'parens');
    await userEvent.click(screen.getByRole('button', { name: /save changes/i }));

    expect(await screen.findByText('Saved')).toBeInTheDocument();
    expect(saved).toEqual(
      expect.objectContaining({ accountingBasis: 'accrual', moneyNegativeDisplay: 'parens' }),
    );
  });

  it('lists trust bank accounts with status badge', async () => {
    server.use(
      http.get('/api/settings/org', () => HttpResponse.json(ORG)),
      http.get('/api/settings/banks', () => HttpResponse.json([ACTIVE_BANK])),
    );
    renderSettings();
    expect(await screen.findByText('Operating Trust')).toBeInTheDocument();
    expect(screen.getByText('••4021')).toBeInTheDocument();
    expect(screen.getByText('Active')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Deactivate' })).toBeInTheDocument();
  });

  it('distinguishes the PM operating account from the operating trust account', async () => {
    server.use(
      http.get('/api/settings/org', () => HttpResponse.json(ORG)),
      http.get('/api/settings/banks', () =>
        HttpResponse.json([
          ACTIVE_BANK,
          {
            ...ACTIVE_BANK,
            id: 'b2',
            name: 'Management Checking',
            purpose: 'operating',
          },
        ]),
      ),
    );

    renderSettings();

    expect(await screen.findByText('Operating trust account')).toBeInTheDocument();
    expect(screen.getByText('PM operating account')).toBeInTheDocument();
    expect(screen.getByText('Bank accounts')).toBeInTheDocument();
    expect(screen.getAllByText('Inside the trust equation.')).not.toHaveLength(0);
    expect(screen.getByText('Outside the trust equation.')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'New account' }));

    expect(
      screen.getByRole('option', {
        name: 'Operating trust account — Inside the trust equation.',
      }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('option', {
        name: 'PM operating account — Outside the trust equation.',
      }),
    ).toBeInTheDocument();

    await userEvent.selectOptions(screen.getByLabelText('Purpose'), 'operating');
    expect(screen.getByText(/management company's own non-trust bank account/i)).toHaveTextContent(
      /outside the trust equation.*cannot be changed after creation/i,
    );
  });

  it('deactivates a bank account and flips the badge to Inactive', async () => {
    server.use(
      http.get('/api/settings/org', () => HttpResponse.json(ORG)),
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/settings/banks', () => HttpResponse.json([ACTIVE_BANK])),
      http.put('/api/settings/banks/:id/active', () =>
        HttpResponse.json({ ...ACTIVE_BANK, isActive: false }),
      ),
    );

    renderSettings();
    await screen.findByText('Operating Trust');

    // After clicking Deactivate the cache is invalidated — return inactive bank on refetch
    server.use(
      http.get('/api/settings/banks', () =>
        HttpResponse.json([{ ...ACTIVE_BANK, isActive: false }]),
      ),
    );

    await userEvent.click(screen.getByRole('button', { name: 'Deactivate' }));
    expect(await screen.findByText('Inactive')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reactivate' })).toBeInTheDocument();
  });

  it('shows inline 409 error when deactivation is blocked', async () => {
    server.use(
      http.get('/api/settings/org', () => HttpResponse.json(ORG)),
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/settings/banks', () => HttpResponse.json([ACTIVE_BANK])),
      http.put('/api/settings/banks/:id/active', () =>
        HttpResponse.json({ detail: 'uncleared items' }, { status: 409 }),
      ),
    );

    renderSettings();
    await screen.findByText('Operating Trust');
    await userEvent.click(screen.getByRole('button', { name: 'Deactivate' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      /clear or reconcile outstanding items/i,
    );
    // Badge stays Active
    expect(screen.getByText('Active')).toBeInTheDocument();
  });

  it('shows the rate as a percentage, stores it in basis points, and keeps the org profile', async () => {
    let saved: Record<string, unknown> | null = null;
    server.use(
      http.get('/api/settings/org', () => HttpResponse.json(ORG)),
      http.get('/api/settings/banks', () => HttpResponse.json([])),
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.put('/api/settings/org', async ({ request }) => {
        saved = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json({ ...ORG, ...saved });
      }),
    );

    renderSettings();

    // Flat is the seeded kind, so the rate input is not on screen until the kind changes.
    expect(await screen.findByLabelText('Flat fee')).toHaveValue(50);
    expect(screen.queryByLabelText('Rate')).not.toBeInTheDocument();

    await userEvent.selectOptions(screen.getByLabelText('Fee type'), 'percent');

    // 500 bps must render as 5, not 500 — the bps/percent boundary is the easiest thing to get
    // wrong here, and getting it wrong overstates every late fee by 100x.
    expect(screen.getByLabelText('Rate')).toHaveValue(5);

    await userEvent.clear(screen.getByLabelText('Rate'));
    await userEvent.type(screen.getByLabelText('Rate'), '7.5');
    await userEvent.click(screen.getByRole('button', { name: /save late-fee policy/i }));

    expect(await screen.findAllByText('Saved')).not.toHaveLength(0);
    expect(saved).toMatchObject({ lateFeeKind: 'percent', lateFeeRateBps: 750 });

    // The handler replaces the profile fields unconditionally, so a late-fee save that omitted them
    // would blank the org's legal name and city. They must ride along untouched.
    expect(saved).toMatchObject({ legalName: 'Tarheel Property Group', city: 'Asheville' });
  });

  beforeEach(() => {
    document.body.innerHTML = '';
  });
});
