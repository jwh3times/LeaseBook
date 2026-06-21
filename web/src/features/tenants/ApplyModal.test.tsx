import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import { ApplyModal } from './ApplyModal';

const BANKS = [
  { id: 'trust1', name: 'Operating Trust', institution: null, mask: null, purpose: 'trust', isActive: true },
  { id: 'dep1', name: 'Deposit Trust', institution: null, mask: null, purpose: 'deposit', isActive: true },
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
        HttpResponse.json({ code: 'account_period_locked', detail: 'locked' }, { status: 409 }),
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
