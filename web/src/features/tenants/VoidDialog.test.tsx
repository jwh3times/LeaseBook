import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import { VoidDialog } from './VoidDialog';

function renderDialog() {
  const onVoided = vi.fn();
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  render(
    <QueryClientProvider client={queryClient}>
      <VoidDialog entryId="e1" onClose={vi.fn()} onVoided={onVoided} />
    </QueryClientProvider>,
  );
  return { onVoided };
}

beforeEach(() => {
  document.body.innerHTML = '';
  vi.clearAllMocks();
});

describe('VoidDialog', () => {
  it('surfaces the M4 account-lock (409) inline and keeps the dialog open', async () => {
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.post('/api/accounting/entries/:entryId/void', () =>
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
    const { onVoided } = renderDialog();

    await userEvent.type(screen.getByLabelText('Reason'), 'entered in error');
    await userEvent.click(screen.getByRole('button', { name: 'Void entry' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/reconciled and locked/i);
    expect(onVoided).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Reason')).toBeInTheDocument();
  });
});
