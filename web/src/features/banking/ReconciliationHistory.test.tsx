import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
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

describe('ReconciliationHistory finalized date', () => {
  const originalTz = process.env.TZ;
  beforeAll(() => {
    process.env.TZ = 'America/New_York';
  });
  afterAll(() => {
    process.env.TZ = originalTz;
  });

  it('shows the date the viewer finalized it on, not the UTC date', async () => {
    // 9 pm Eastern on 12 October is already 13 October in UTC.
    server.use(
      http.get('/api/accounting/reconciliations', () =>
        HttpResponse.json({
          rows: [
            {
              id: 'rec1',
              status: 'finalized',
              bankAccountId: 'acct1',
              year: 2026,
              month: 9,
              statementEndingBalance: 12000,
              finalizedAt: '2026-10-13T01:00:00Z',
              hasReport: false,
            },
          ],
        }),
      ),
    );
    renderHistory();

    expect(await screen.findByText('Finalized 2026-10-12')).toBeInTheDocument();
  });
});

describe('ReconciliationHistory unlock (#554)', () => {
  const FINALIZED = {
    id: 'rec-sep',
    status: 'finalized',
    bankAccountId: 'acct1',
    year: 2026,
    month: 9,
    statementEndingBalance: 12000,
    finalizedAt: '2026-10-01T15:00:00Z',
    hasReport: true,
  };
  const REOPENED = { ...FINALIZED, id: 'rec-aug', month: 8, status: 'reopened' };

  function signedInAs(role: 'PMAdmin' | 'PMStaff') {
    return http.get('/api/auth/me', () => HttpResponse.json({ email: 'a@b.test', role }));
  }

  /** History that answers from `rows`, so a test can change what the next read returns. */
  function historyOf(rows: () => unknown[]) {
    return http.get('/api/accounting/reconciliations', () => HttpResponse.json({ rows: rows() }));
  }

  const unlockButton = () => screen.findByRole('button', { name: 'Unlock Sep 2026' });

  it('offers an administrator Unlock on a finalized month only', async () => {
    server.use(
      signedInAs('PMAdmin'),
      historyOf(() => [FINALIZED, REOPENED]),
    );
    renderHistory();

    expect(await unlockButton()).toBeInTheDocument();
    // A reopened month is already unlocked: nothing to offer.
    expect(screen.queryByRole('button', { name: 'Unlock Aug 2026' })).toBeNull();
  });

  it('never offers staff an Unlock', async () => {
    server.use(
      signedInAs('PMStaff'),
      historyOf(() => [FINALIZED]),
    );
    renderHistory();

    await screen.findByText('Sep 2026');
    expect(screen.queryByRole('button', { name: /^Unlock/ })).toBeNull();
  });

  it('requires a reason and sends nothing without one', async () => {
    let sent = 0;
    server.use(
      signedInAs('PMAdmin'),
      historyOf(() => [FINALIZED]),
      http.post('/api/accounting/reconciliations/:id/unlock', () => {
        sent += 1;
        return HttpResponse.json({});
      }),
    );
    renderHistory();

    await userEvent.click(await unlockButton());
    const dialog = screen.getByRole('dialog', { name: 'Unlock Sep 2026' });
    await userEvent.click(within(dialog).getByRole('button', { name: 'Unlock month' }));

    expect(within(dialog).getByRole('alert')).toHaveTextContent('A reason is required');
    expect(sent).toBe(0);
  });

  it('unlocks with the reason and shows the month reopened', async () => {
    let status = 'finalized';
    let body: unknown;
    server.use(
      signedInAs('PMAdmin'),
      historyOf(() => [{ ...FINALIZED, status }]),
      http.post('/api/accounting/reconciliations/:id/unlock', async ({ request, params }) => {
        body = { id: params.id, ...((await request.json()) as object) };
        status = 'reopened';
        return HttpResponse.json({ ...FINALIZED, status, clearedBalance: 12000, difference: 0 });
      }),
    );
    renderHistory();

    await userEvent.click(await unlockButton());
    // The reason field takes focus, and Enter confirms.
    await userEvent.keyboard('Bank fee missing from the register{Enter}');

    expect(await screen.findByText('Reopened')).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Unlock Sep 2026' })).toBeNull();
    expect(body).toMatchObject({ id: 'rec-sep', reason: 'Bank fee missing from the register' });
  });

  it('keeps the dialog open on a refusal the session no longer carries the role for (403)', async () => {
    server.use(
      signedInAs('PMAdmin'),
      historyOf(() => [FINALIZED]),
      http.post(
        '/api/accounting/reconciliations/:id/unlock',
        () => new HttpResponse(null, { status: 403 }),
      ),
    );
    renderHistory();

    await userEvent.click(await unlockButton());
    await userEvent.keyboard('Correction{Enter}');

    const dialog = screen.getByRole('dialog', { name: 'Unlock Sep 2026' });
    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'Only an administrator can unlock a reconciliation.',
    );
  });

  it('keeps the dialog open and says why when the month is no longer finalized (409)', async () => {
    server.use(
      signedInAs('PMAdmin'),
      historyOf(() => [FINALIZED]),
      http.post('/api/accounting/reconciliations/:id/unlock', () =>
        HttpResponse.json(
          {
            code: 'reconciliation_state',
            detail: 'That reconciliation is not finalized — there is nothing to unlock.',
          },
          { status: 409 },
        ),
      ),
    );
    renderHistory();

    await userEvent.click(await unlockButton());
    await userEvent.keyboard('Correction{Enter}');

    const dialog = screen.getByRole('dialog', { name: 'Unlock Sep 2026' });
    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'That reconciliation is not finalized — there is nothing to unlock.',
    );
  });

  it('cancels on Escape and returns focus to Unlock', async () => {
    server.use(
      signedInAs('PMAdmin'),
      historyOf(() => [FINALIZED]),
    );
    renderHistory();

    const unlock = await unlockButton();
    await userEvent.click(unlock);
    expect(screen.getByRole('dialog', { name: 'Unlock Sep 2026' })).toBeInTheDocument();
    await userEvent.keyboard('{Escape}');

    expect(screen.queryByRole('dialog')).toBeNull();
    expect(unlock).toHaveFocus();
  });
});
