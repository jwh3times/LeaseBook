import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
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

describe('ReconciliationHistory finalize again (#656)', () => {
  const REOPENED = {
    id: 'rec-aug',
    status: 'reopened',
    bankAccountId: 'acct1',
    year: 2026,
    month: 8,
    statementEndingBalance: 12000,
    finalizedAt: null,
    hasReport: true,
  };
  const FINALIZED = { ...REOPENED, id: 'rec-sep', month: 9, status: 'finalized' };

  // What the August statement can be finalized against: $11,500.00 already stamped for August and the
  // months before it, plus four unreconciled lines dated through 31 August. Two are one payout (ADR-053).
  const LINES = {
    reconciliation: { ...REOPENED, clearedBalance: 11500, difference: 500 },
    reconciledThrough: 11500,
    lines: [
      {
        journalLineId: 'l-dep',
        entryDate: '2026-08-04',
        description: 'Deposit — Unit 2B rent',
        amount: 300,
        status: 'cleared',
        payoutReference: null,
      },
      {
        journalLineId: 'l-chk',
        entryDate: '2026-08-20',
        description: 'Check 1041 — plumber',
        amount: -100,
        status: 'uncleared',
        payoutReference: null,
      },
      {
        journalLineId: 'l-po1',
        entryDate: '2026-08-28',
        description: 'Payout — Unit 4A',
        amount: 250,
        status: 'cleared',
        payoutReference: 'po_1',
      },
      {
        journalLineId: 'l-po2',
        entryDate: '2026-08-28',
        description: 'Payout — Unit 5C',
        amount: 50,
        status: 'cleared',
        payoutReference: 'po_1',
      },
    ],
  };

  function staff() {
    return http.get('/api/auth/me', () =>
      HttpResponse.json({ email: 'a@b.test', role: 'PMStaff' }),
    );
  }

  function historyOf(rows: () => unknown[]) {
    return http.get('/api/accounting/reconciliations', () => HttpResponse.json({ rows: rows() }));
  }

  const linesOf = () =>
    http.get('/api/accounting/reconciliations/:id/lines', () => HttpResponse.json(LINES));

  const openAugust = async () => {
    await userEvent.click(await screen.findByRole('button', { name: 'Finalize Aug 2026 again' }));
    return screen.findByRole('region', { name: 'Finalize Aug 2026 again' });
  };

  it('offers staff Finalize again on a reopened month only, and opens it in place', async () => {
    server.use(
      staff(),
      historyOf(() => [FINALIZED, REOPENED]),
      linesOf(),
    );
    renderHistory();

    const panel = await openAugust();
    expect(screen.queryByRole('button', { name: 'Finalize Sep 2026 again' })).toBeNull();

    // The stored statement balance is prefilled, and nothing is ticked: the difference is the
    // statement less what is already reconciled through August.
    expect(within(panel).getByLabelText('Statement ending balance')).toHaveValue('12000.00');
    expect(within(panel).getByLabelText('Difference')).toHaveTextContent(/^\+\$500\.00$/);
    for (const box of within(panel).getAllByRole('checkbox')) expect(box).not.toBeChecked();
    expect(within(panel).getByText('Check 1041 — plumber')).toBeInTheDocument();
  });

  it('ticks a payout whole, drives the difference to zero, and finalizes exactly the ticked lines', async () => {
    let status = 'reopened';
    let finalizeBody: unknown;
    let started = 0;
    server.use(
      staff(),
      historyOf(() => [{ ...REOPENED, status }]),
      linesOf(),
      http.post('/api/accounting/reconciliations', () => {
        started += 1;
        return HttpResponse.json({});
      }),
      http.post('/api/accounting/reconciliations/:id/finalize', async ({ request, params }) => {
        finalizeBody = { id: params.id, ...((await request.json()) as object) };
        status = 'finalized';
        return HttpResponse.json({ ...LINES.reconciliation, status, difference: 0 });
      }),
    );
    renderHistory();

    const panel = await openAugust();
    const finalize = within(panel).getByRole('button', { name: 'Finalize Aug 2026' });
    const difference = within(panel).getByLabelText('Difference');

    // One payout's lines clear together, so ticking one ticks both: 250 + 50.
    await userEvent.click(within(panel).getByRole('checkbox', { name: /Payout — Unit 4A/ }));
    expect(within(panel).getByRole('checkbox', { name: /Payout — Unit 5C/ })).toBeChecked();
    expect(difference).toHaveTextContent(/^\+\$200\.00$/);
    expect(finalize).toBeDisabled();

    await userEvent.click(within(panel).getByRole('checkbox', { name: /Deposit — Unit 2B/ }));
    expect(difference).toHaveTextContent(/^−\$100\.00$/);

    // The uncleared check the statement shows: ticking it is what clears it.
    await userEvent.click(within(panel).getByRole('checkbox', { name: /Check 1041/ }));
    expect(difference).toHaveTextContent(/^\$0\.00$/);
    expect(finalize).toBeEnabled();

    await userEvent.click(finalize);

    expect(await screen.findByText('Finalized')).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Finalize Aug 2026 again' })).toBeNull();
    expect(finalizeBody).toMatchObject({ id: 'rec-aug' });
    expect([...(finalizeBody as { assignedLineIds: string[] }).assignedLineIds].sort()).toEqual([
      'l-chk',
      'l-dep',
      'l-po1',
      'l-po2',
    ]);
    // The statement balance was the stored one, so the month was not re-entered.
    expect(started).toBe(0);
  });

  it('sets a corrected statement balance before finalizing against it', async () => {
    const calls: { route: string; body: unknown }[] = [];
    server.use(
      staff(),
      historyOf(() => [REOPENED]),
      linesOf(),
      http.post('/api/accounting/reconciliations', async ({ request }) => {
        calls.push({ route: 'start', body: await request.json() });
        return HttpResponse.json({ ...LINES.reconciliation, statementEndingBalance: 11800 });
      }),
      http.post('/api/accounting/reconciliations/:id/finalize', async ({ request }) => {
        calls.push({ route: 'finalize', body: await request.json() });
        return HttpResponse.json({ ...LINES.reconciliation, status: 'finalized', difference: 0 });
      }),
    );
    renderHistory();

    const panel = await openAugust();
    const statement = within(panel).getByLabelText('Statement ending balance');
    await userEvent.clear(statement);
    await userEvent.type(statement, '11800');
    await userEvent.click(within(panel).getByRole('checkbox', { name: /Deposit — Unit 2B/ }));
    expect(within(panel).getByLabelText('Difference')).toHaveTextContent(/^\$0\.00$/);

    await userEvent.click(within(panel).getByRole('button', { name: 'Finalize Aug 2026' }));

    await waitFor(() => expect(calls).toHaveLength(2));
    expect(calls).toEqual([
      {
        route: 'start',
        body: { bankAccountId: 'acct1', year: 2026, month: 8, statementEndingBalance: 11800 },
      },
      { route: 'finalize', body: { assignedLineIds: ['l-dep'] } },
    ]);
  });

  it('takes focus when it opens and gives it back to its button on Cancel', async () => {
    server.use(
      staff(),
      historyOf(() => [REOPENED]),
      linesOf(),
    );
    renderHistory();

    const button = await screen.findByRole('button', { name: 'Finalize Aug 2026 again' });
    expect(button).toHaveAttribute('aria-expanded', 'false');
    await userEvent.click(button);

    const panel = await screen.findByRole('region', { name: 'Finalize Aug 2026 again' });
    expect(within(panel).getByRole('heading', { name: 'Finalize Aug 2026 again' })).toHaveFocus();
    expect(button).toHaveAttribute('aria-expanded', 'true');

    await userEvent.click(within(panel).getByRole('button', { name: 'Cancel' }));

    expect(screen.queryByRole('region', { name: 'Finalize Aug 2026 again' })).toBeNull();
    expect(button).toHaveFocus();
  });

  it('shows why the lines could not be read, retries in place, and offers no finalize meanwhile', async () => {
    const reference = '99887766998877669988776699887766';
    let attempt = 0;
    server.use(
      staff(),
      historyOf(() => [REOPENED]),
      http.get('/api/accounting/reconciliations/:id/lines', () => {
        attempt += 1;
        return attempt === 1
          ? HttpResponse.json(
              { detail: 'The bank lines are unavailable.', correlationId: reference },
              { status: 503 },
            )
          : HttpResponse.json(LINES);
      }),
    );
    renderHistory();

    const panel = await openAugust();
    expect(await within(panel).findByText("Couldn't load the lines")).toBeInTheDocument();
    expect(within(panel).getByRole('alert')).toHaveTextContent('The bank lines are unavailable.');
    expect(within(panel).getByText(`Reference: ${reference}`)).toBeInTheDocument();
    expect(within(panel).getByRole('button', { name: 'Finalize Aug 2026' })).toBeDisabled();
    // No difference is claimed from a balance that was never read.
    expect(within(panel).queryByLabelText('Difference')).toBeNull();

    await userEvent.click(within(panel).getByRole('button', { name: 'Retry' }));

    expect(await within(panel).findByLabelText('Difference')).toHaveTextContent(/^\+\$500\.00$/);
  });

  it('shows the finalize pending, holds the lines still meanwhile, and lands focus on the closed month', async () => {
    let status = 'reopened';
    let release: () => void = () => {};
    server.use(
      staff(),
      historyOf(() => [{ ...REOPENED, status }]),
      linesOf(),
      http.post('/api/accounting/reconciliations/:id/finalize', async () => {
        await new Promise<void>((resolve) => {
          release = resolve;
        });
        status = 'finalized';
        return HttpResponse.json({ ...LINES.reconciliation, status, difference: 0 });
      }),
    );
    renderHistory();

    const panel = await openAugust();
    for (const name of [/Deposit — Unit 2B/, /Check 1041/, /Payout — Unit 4A/])
      await userEvent.click(within(panel).getByRole('checkbox', { name }));
    await userEvent.click(within(panel).getByRole('button', { name: 'Finalize Aug 2026' }));

    // In flight: the button says so, and nothing the request was built from can change under it.
    expect(await within(panel).findByRole('button', { name: 'Finalizing…' })).toBeDisabled();
    for (const box of within(panel).getAllByRole('checkbox')) expect(box).toBeDisabled();
    expect(within(panel).getByLabelText('Statement ending balance')).toBeDisabled();

    release();

    // The view and its button are gone, so the keyboard lands on the month it just closed.
    expect(await screen.findByText('Finalized')).toBeInTheDocument();
    expect(screen.getByText('Aug 2026')).toHaveFocus();
  });

  it('reads the lines again after a refusal, and finalizes only lines still on offer', async () => {
    let reads = 0;
    let finalizeBody: { assignedLineIds?: string[] } | undefined;
    let attempt = 0;
    server.use(
      staff(),
      historyOf(() => [REOPENED]),
      http.get('/api/accounting/reconciliations/:id/lines', () => {
        reads += 1;
        // Meanwhile another month reconciled the plumber's check, so it is no longer on offer.
        return HttpResponse.json(
          reads === 1
            ? LINES
            : { ...LINES, lines: LINES.lines.filter((l) => l.journalLineId !== 'l-chk') },
        );
      }),
      http.post('/api/accounting/reconciliations', () => HttpResponse.json({})),
      http.post('/api/accounting/reconciliations/:id/finalize', async ({ request }) => {
        attempt += 1;
        if (attempt === 1)
          return HttpResponse.json(
            {
              code: 'reconciliation_line_not_assignable',
              detail:
                'A line chosen for 2026-08 is already reconciled in a month’s reconciliation.',
            },
            { status: 422 },
          );
        finalizeBody = (await request.json()) as { assignedLineIds?: string[] };
        return HttpResponse.json({ ...LINES.reconciliation, status: 'finalized', difference: 0 });
      }),
    );
    renderHistory();

    const panel = await openAugust();
    for (const name of [/Deposit — Unit 2B/, /Check 1041/, /Payout — Unit 4A/])
      await userEvent.click(within(panel).getByRole('checkbox', { name }));
    await userEvent.click(within(panel).getByRole('button', { name: 'Finalize Aug 2026' }));
    await within(panel).findByRole('alert');

    await waitFor(() =>
      expect(within(panel).queryByRole('checkbox', { name: /Check 1041/ })).toBeNull(),
    );
    // Without the check the ticked lines come to $600.00, which the statement now has to show.
    const statement = within(panel).getByLabelText('Statement ending balance');
    await userEvent.clear(statement);
    await userEvent.type(statement, '12100');
    expect(within(panel).getByLabelText('Difference')).toHaveTextContent(/^\$0\.00$/);
    await userEvent.click(within(panel).getByRole('button', { name: 'Finalize Aug 2026' }));

    await waitFor(() => expect(finalizeBody).toBeDefined());
    expect([...(finalizeBody?.assignedLineIds ?? [])].sort()).toEqual(['l-dep', 'l-po1', 'l-po2']);
  });

  it.each([
    [
      409,
      'reconciliation_later_month_finalized',
      '2026-09 is finalized for this account; unlock it before finalizing an earlier month.',
    ],
    [
      409,
      'reconciliation_earlier_month_reopened',
      '2026-07 is reopened for this account; finalize it again before a later month.',
    ],
    [
      422,
      'reconciliation_line_not_assignable',
      'A line dated 2026-09-02 is after the end of 2026-08, so it cannot be reconciled in that month.',
    ],
    [
      422,
      'reconciliation_period_in_future',
      "2026-12 is after the organization's current month, 2026-11, so it cannot be reconciled yet.",
    ],
    [
      422,
      'reconciliation_lines_required',
      "2026-08 is not the organization's current month, 2026-11, so finalizing it must name the lines its statement shows.",
    ],
    [
      409,
      'reconciliation_unbalanced',
      'The reconciliation difference is 25.00; clear items until it is 0.00 before finalizing.',
    ],
  ])('keeps the view open and shows the refusal (%i %s)', async (statusCode, code, detail) => {
    const reference = 'aabbccddaabbccddaabbccddaabbccdd';
    server.use(
      staff(),
      historyOf(() => [REOPENED]),
      linesOf(),
      http.post('/api/accounting/reconciliations/:id/finalize', () =>
        HttpResponse.json({ code, detail, correlationId: reference }, { status: statusCode }),
      ),
    );
    renderHistory();

    const panel = await openAugust();
    // Each box once: the payout's second line ticks with its first.
    for (const name of [/Deposit — Unit 2B/, /Check 1041/, /Payout — Unit 4A/])
      await userEvent.click(within(panel).getByRole('checkbox', { name }));
    await userEvent.click(within(panel).getByRole('button', { name: 'Finalize Aug 2026' }));

    const alert = await within(panel).findByRole('alert');
    expect(alert).toHaveTextContent(detail);
    expect(alert).toHaveTextContent(`Reference: ${reference}`);
    // What was ticked survives the refusal, so acting on it does not mean ticking it all again.
    for (const box of within(panel).getAllByRole('checkbox')) expect(box).toBeChecked();
  });
});
