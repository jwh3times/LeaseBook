import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import { ApplyModal } from './ApplyModal';

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

function renderModal() {
  const onApplied = vi.fn();
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  render(
    <QueryClientProvider client={queryClient}>
      <ApplyModal tenantId="t1" initialKind="deposit" onClose={vi.fn()} onApplied={onApplied} />
    </QueryClientProvider>,
  );
  return { onApplied };
}

beforeEach(() => {
  document.body.innerHTML = '';
  vi.clearAllMocks();
});

describe('ApplyModal', () => {
  it('surfaces the M4 account-lock (409) inline and keeps the modal open', async () => {
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/settings/banks', () => HttpResponse.json(BANKS)),
      http.post('/api/accounting/tenants/:tenantId/deposit-applications', () =>
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
    const { onApplied } = renderModal();

    await screen.findByText(/Operating Trust/);
    await userEvent.type(screen.getByLabelText('Amount'), '100');
    await userEvent.click(screen.getByRole('button', { name: 'Apply' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/reconciled and locked/i);
    expect(onApplied).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Amount')).toBeInTheDocument();
  });
});

describe('ApplyModal when the trust banks cannot be read', () => {
  it('reports the read failure rather than missing configuration', async () => {
    let posted = false;
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/settings/banks', () => new HttpResponse(null, { status: 503 })),
      http.post('/api/accounting/tenants/:tenantId/deposit-applications', () => {
        posted = true;
        return HttpResponse.json({ entryId: 'e1' });
      }),
    );
    renderModal();

    expect(await screen.findByRole('alert')).toBeInTheDocument();

    // "No trust bank is configured" would send the operator to Settings to fix a bank that is
    // already there. The org's configuration is unknown, not known to be empty.
    expect(screen.queryByText(/no trust bank is configured/i)).toBeNull();

    // Money must not move against a bank resolved from an unread list, by either route in.
    await userEvent.type(screen.getByLabelText(/amount/i), '100');
    expect(screen.getByRole('button', { name: /^apply$/i })).toBeDisabled();
    await userEvent.keyboard('{Enter}');
    expect(posted).toBe(false);
  });

  it('still reports genuine missing configuration on a successful empty list', async () => {
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/settings/banks', () => HttpResponse.json([])),
    );
    renderModal();

    await userEvent.type(await screen.findByLabelText(/amount/i), '100');
    await userEvent.keyboard('{Enter}');

    expect(await screen.findByText(/no trust bank is configured/i)).toBeInTheDocument();
  });

  it('recovers once the banks read succeeds', async () => {
    let attempt = 0;
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/settings/banks', () => {
        attempt += 1;
        return attempt === 1 ? new HttpResponse(null, { status: 503 }) : HttpResponse.json(BANKS);
      }),
    );
    renderModal();

    await userEvent.click(await screen.findByRole('button', { name: /retry/i }));

    expect(await screen.findByRole('button', { name: /^apply$/i })).toBeEnabled();
  });
});
