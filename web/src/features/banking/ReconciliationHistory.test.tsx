import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { ReconciliationHistory } from './ReconciliationHistory';

function renderHistory() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <ReconciliationHistory bankAccountId="acct1" />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  document.body.innerHTML = '';
});

describe('ReconciliationHistory header count', () => {
  it('does not claim zero reconciliations when the read failed', async () => {
    server.use(
      http.get('/api/accounting/reconciliations', () => new HttpResponse(null, { status: 503 })),
    );
    renderHistory();

    // The body's own error branch is the established EmptyState pattern for this surface.
    await screen.findByText(/couldn't load history/i);

    // "0 reconciliations" is a confirmed fact about a locked-period audit trail. An unread list is
    // not that fact, and on this surface the difference is the difference between "nothing to show"
    // and "the record could not be reached".
    expect(screen.queryByText(/^0 reconciliations$/)).toBeNull();
  });

  it('does not claim zero while the read is still in flight', () => {
    server.use(
      http.get('/api/accounting/reconciliations', async () => {
        await new Promise((resolve) => setTimeout(resolve, 50));
        return HttpResponse.json({ rows: [] });
      }),
    );
    renderHistory();

    expect(screen.queryByText(/^0 reconciliations$/)).toBeNull();
  });

  it('reports a genuine zero once the empty list is actually read', async () => {
    server.use(http.get('/api/accounting/reconciliations', () => HttpResponse.json({ rows: [] })));
    renderHistory();

    expect(await screen.findByText(/^0 reconciliations$/)).toBeInTheDocument();
  });
});

describe('ReconciliationHistory body when the read failed', () => {
  it('carries the support reference and retries in place', async () => {
    const reference = '11223344112233441122334411223344';
    let attempt = 0;
    server.use(
      http.get('/api/accounting/reconciliations', () => {
        attempt += 1;
        return attempt === 1
          ? HttpResponse.json(
              { detail: 'The reconciliation log is unavailable.', correlationId: reference },
              { status: 503 },
            )
          : HttpResponse.json({
              rows: [
                {
                  id: 'rec1',
                  statementDate: '2026-02-28',
                  statementBalance: 12000,
                  clearedBalance: 12000,
                  finalizedAt: '2026-03-01T10:00:00Z',
                  itemCount: 8,
                },
              ],
            });
      }),
    );
    renderHistory();

    expect(await screen.findByText("Couldn't load history")).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent('The reconciliation log is unavailable.');
    expect(screen.getByText(`Reference: ${reference}`)).toBeInTheDocument();
    expect(screen.queryByText('No reconciliations yet')).toBeNull();

    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));

    expect(await screen.findByText(/^1 reconciliation/)).toBeInTheDocument();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
