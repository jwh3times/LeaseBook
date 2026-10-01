import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import { VoidDialog } from './VoidDialog';

function renderDialog(description?: string | null) {
  const onVoided = vi.fn();
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  render(
    <QueryClientProvider client={queryClient}>
      <VoidDialog entryId="e1" description={description} onClose={vi.fn()} onVoided={onVoided} />
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

    await userEvent.type(screen.getByLabelText('Reason (internal note)'), 'entered in error');
    await userEvent.click(screen.getByRole('button', { name: 'Void entry' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/reconciled and locked/i);
    expect(onVoided).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Reason (internal note)')).toBeInTheDocument();
  });
});

// #468 (ADR-047): a void's reason is staff-only. The owner statement shows what was corrected, not why.
describe('VoidDialog reason as an internal note', () => {
  it('labels the reason staff-only and says what the owner statement will show', () => {
    renderDialog('June rent');

    const reason = screen.getByLabelText('Reason (internal note)');
    expect(reason).toHaveAccessibleDescription('Staff only');
    expect(screen.getByText('“Void — June rent”')).toBeInTheDocument();
    expect(screen.getByText(/kept as a staff-only internal note/)).toBeInTheDocument();
    // "recorded in its history" never said who could read it.
    expect(screen.queryByText(/recorded in its history/)).toBeNull();
  });

  it('matches the server’s bare “Void” when the entry had no description', () => {
    renderDialog(null);
    expect(screen.getByText('“Void”')).toBeInTheDocument();
  });

  it('posts the reason, which the server stores as the reversal’s internal note', async () => {
    let body: Record<string, unknown> | undefined;
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.post('/api/accounting/entries/:entryId/void', async ({ request }) => {
        body = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json({ entryId: 'rev1' });
      }),
    );
    const { onVoided } = renderDialog('June rent');

    await userEvent.type(screen.getByLabelText('Reason (internal note)'), 'keyed twice');
    await userEvent.keyboard('{Enter}');

    await vi.waitFor(() => expect(onVoided).toHaveBeenCalledWith('rev1'));
    expect(body).toMatchObject({ entryId: 'e1', reason: 'keyed twice' });
  });
});

// #473: a refund check is voided from its check record, never through the generic entry void.
describe('VoidDialog on a refund check entry', () => {
  it('points the user to the refund check list on Banking instead of a generic error', async () => {
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.post('/api/accounting/entries/:entryId/void', () =>
        HttpResponse.json(
          {
            code: 'refund_check_void_required',
            detail: 'This entry is a refund check. Void it from the check instead.',
          },
          { status: 409 },
        ),
      ),
    );
    const onVoided = vi.fn();
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter>
          <VoidDialog
            entryId="e1"
            description="Refund check #1043 — security deposit"
            onClose={vi.fn()}
            onVoided={onVoided}
          />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    await userEvent.type(screen.getByLabelText('Reason (internal note)'), 'misprint');
    await userEvent.click(screen.getByRole('button', { name: 'Void entry' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('This entry is refund check #1043.');
    expect(alert).toHaveTextContent('Void it from the refund check list on Banking');
    expect(screen.getByRole('link', { name: 'Open refund checks on Banking' })).toHaveAttribute(
      'href',
      '/banking',
    );
    expect(onVoided).not.toHaveBeenCalled();
  });
});
