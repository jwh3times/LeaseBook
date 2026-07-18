import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import { CompliancePackPanel } from './CompliancePackPanel';
import type { ReportDescriptor } from './reports';

const REPORT: ReportDescriptor = {
  id: 'compliance-pack',
  name: 'Trust compliance pack',
  category: 'Compliance',
  description: 'Audit-ready ZIP for a closed period',
  favorite: false,
  icon: 'doc',
  acceptedFilters: ['bankAccountId', 'from', 'to'],
};

const BANKS_RESPONSE = {
  rows: [
    {
      bankAccountId: 'bank-1',
      name: 'Operating Trust',
      book: 5000,
      cleared: 4800,
      uncleared: 200,
      unclearedCount: 2,
    },
  ],
};

function banksHandler() {
  return http.get('/api/accounting/banks/balances', () => HttpResponse.json(BANKS_RESPONSE));
}

function renderPanel(isAdmin: boolean) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <CompliancePackPanel report={REPORT} isAdmin={isAdmin} />
    </QueryClientProvider>,
  );
}

// Open the trust-account chip and pick Operating Trust.
async function selectOperatingTrust() {
  await userEvent.click(screen.getByText('Trust account').closest('button')!);
  const dialog = await screen.findByRole('dialog', { name: 'Select Trust account' });
  await userEvent.click(within(dialog).getByRole('button', { name: 'Operating Trust' }));
}

beforeEach(() => {
  document.body.innerHTML = '';
  vi.clearAllMocks();
  globalThis.URL.createObjectURL = vi.fn(() => 'blob:test');
  globalThis.URL.revokeObjectURL = vi.fn();
});

describe('CompliancePackPanel', () => {
  it('renders the download action, trust account chip, and from/to date inputs for an admin', () => {
    server.use(banksHandler());
    renderPanel(true);

    expect(screen.getByRole('button', { name: 'Download pack' })).toBeInTheDocument();
    expect(screen.getByText('Trust account')).toBeInTheDocument();
    expect(screen.getByLabelText('From date')).toBeInTheDocument();
    expect(screen.getByLabelText('To date')).toBeInTheDocument();
  });

  it('gates non-admins with an admin-only message and no download action', () => {
    server.use(banksHandler());
    renderPanel(false);

    expect(screen.getByText('Admin only')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Download pack' })).not.toBeInTheDocument();
  });

  it('keeps the download disabled until a trust account is chosen', async () => {
    server.use(banksHandler());
    renderPanel(true);

    expect(screen.getByRole('button', { name: 'Download pack' })).toBeDisabled();
    await selectOperatingTrust();
    expect(screen.getByRole('button', { name: 'Download pack' })).toBeEnabled();
  });

  it('downloads a ZIP for the selected account and confirms with a non-color-only status', async () => {
    const requestedBankIds: string[] = [];
    server.use(
      banksHandler(),
      http.get('/api/reports/compliance-pack', ({ request }) => {
        requestedBankIds.push(new URL(request.url).searchParams.get('bankAccountId') ?? '');
        return new HttpResponse(new Blob(['PKzip-bytes']), {
          headers: { 'Content-Type': 'application/zip' },
        });
      }),
    );
    renderPanel(true);

    await selectOperatingTrust();
    await userEvent.click(screen.getByRole('button', { name: 'Download pack' }));

    // The success status carries text (an icon alone would fail WCAG 1.4.1).
    expect(await screen.findByText(/downloading/i)).toBeInTheDocument();
    expect(requestedBankIds).toContain('bank-1');
  });

  it('surfaces a clear period-not-closed message on 422', async () => {
    server.use(
      banksHandler(),
      http.get('/api/reports/compliance-pack', () =>
        HttpResponse.json(
          { title: 'period_not_closed', detail: 'The period ending 2026-12 is not locked.' },
          { status: 422 },
        ),
      ),
    );
    renderPanel(true);

    await selectOperatingTrust();
    await userEvent.click(screen.getByRole('button', { name: 'Download pack' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(/isn't closed yet/i);
  });
});
