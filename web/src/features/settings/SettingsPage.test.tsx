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
    expect(saved).toEqual(expect.objectContaining({ accountingBasis: 'accrual', moneyNegativeDisplay: 'parens' }));
  });

  it('lists trust bank accounts', async () => {
    server.use(
      http.get('/api/settings/org', () => HttpResponse.json(ORG)),
      http.get('/api/settings/banks', () =>
        HttpResponse.json([{ id: 'b1', name: 'Operating Trust', institution: 'First Citizens', mask: '4021', purpose: 'trust', isActive: true }]),
      ),
    );
    renderSettings();
    expect(await screen.findByText('Operating Trust')).toBeInTheDocument();
    expect(screen.getByText('••4021')).toBeInTheDocument();
  });

  beforeEach(() => {
    document.body.innerHTML = '';
  });
});
