import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import { describe, expect, it } from 'vitest';
import type { BulkRunDetailResponse, BulkRunItemSpa } from '@/api';
import { server } from '@/test/mocks/server';
import { RunDetailPage } from './RunDetailPage';

const REFERENCE = 'c1c2c3c4c1c2c3c4c1c2c3c4c1c2c3c4';

const item = (
  over: Partial<BulkRunItemSpa> & Pick<BulkRunItemSpa, 'id' | 'outcome'>,
): BulkRunItemSpa => ({
  targetKind: 'Lease',
  targetId: `target-${over.id}`,
  amount: 0,
  reason: null,
  targetName: null,
  unitLabel: null,
  tenantId: null,
  ...over,
});

// In the server's order (excluded, then skipped, then posted), which the page keeps as it is.
const RENT_RUN: BulkRunDetailResponse = {
  run: {
    id: 'run-1',
    runType: 'Rent',
    periodYear: 2026,
    periodMonth: 3,
    summaryJson: JSON.stringify({ total: 1380 }),
    createdAt: '2026-03-01T14:00:00Z',
  },
  items: [
    item({
      id: 'i1',
      outcome: 'Excluded',
      reason: 'lease_not_in_schedule',
      targetName: 'Carol Ended',
      unitLabel: '#C',
      tenantId: 'tenant-carol',
    }),
    item({ id: 'i2', outcome: 'Excluded', reason: 'lease_not_in_schedule' }),
    item({
      id: 'i3',
      outcome: 'Skipped',
      reason: 'already_charged_in_period',
      targetName: 'Alice Full',
      unitLabel: '#A',
      tenantId: 'tenant-alice',
    }),
    item({
      id: 'i4',
      outcome: 'Posted',
      amount: 1380,
      targetName: 'Bob Full',
      unitLabel: '#B',
      tenantId: 'tenant-bob',
    }),
  ],
};

const DISBURSEMENT_RUN: BulkRunDetailResponse = {
  run: {
    id: 'run-2',
    runType: 'Disbursement',
    periodYear: 2026,
    periodMonth: 3,
    summaryJson: JSON.stringify({ posted: 0, skipped: 0, excluded: 2, total: 0 }),
    createdAt: '2026-03-31T18:00:00Z',
  },
  items: [
    item({
      id: 'o1',
      targetKind: 'Owner',
      targetId: 'owner-still',
      outcome: 'Excluded',
      reason: 'below_reserve_floor',
      targetName: 'Stillwater Properties',
    }),
    item({ id: 'o2', targetKind: 'Owner', outcome: 'Excluded', reason: 'owner_not_found' }),
  ],
};

function serve(detail: BulkRunDetailResponse, coverage: { calls: number } = { calls: 0 }) {
  server.use(
    http.get('/api/operations/runs/:id', ({ params }) =>
      params.id === detail.run.id
        ? HttpResponse.json(detail)
        : HttpResponse.json(
            { code: 'run_not_found', detail: 'That run was not found.' },
            { status: 404 },
          ),
    ),
    http.get('/api/statements/issued-coverage', () => {
      coverage.calls += 1;
      return HttpResponse.json({
        rows: [
          {
            ownerId: 'owner-1',
            ownerName: 'Harborview Holdings',
            basis: 'accrual',
            propertyId: null,
            propertyAddress: null,
            issuedYear: 2026,
            issuedMonth: 3,
          },
        ],
      });
    }),
  );
  return coverage;
}

function Where() {
  const location = useLocation();
  return <p>Operations page at {location.pathname + location.search}</p>;
}

function open(runId: string) {
  render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter initialEntries={[`/operations/runs/${runId}`]}>
        <Routes>
          <Route path="/operations/runs/:id" element={<RunDetailPage />} />
          <Route path="/operations" element={<Where />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const rows = () => screen.getAllByRole('row').slice(1);

describe('RunDetailPage — what a run did to each target', () => {
  it('lists every target in the order the server gave, with its outcome in words and the reason for what did not post', async () => {
    serve(RENT_RUN);
    open('run-1');

    expect(await screen.findByRole('heading', { name: 'Rent run — March 2026' })).toBeVisible();
    expect(rows().map((r) => r.textContent)).toEqual([
      expect.stringContaining('Carol Ended'),
      expect.stringContaining('Lease not found'),
      expect.stringContaining('Alice Full'),
      expect.stringContaining('Bob Full'),
    ]);

    const carol = rows()[0]!;
    expect(within(carol).getByText('Excluded')).toBeVisible();
    expect(within(carol).getByText('Lease not active in this period')).toBeVisible();
    expect(carol).toHaveTextContent('#C');

    const alice = rows()[2]!;
    expect(within(alice).getByText('Skipped')).toBeVisible();
    expect(within(alice).getByText('Rent already charged for this period')).toBeVisible();

    const bob = rows()[3]!;
    expect(within(bob).getByText('Posted')).toBeVisible();
    expect(bob).toHaveTextContent('$1,380.00');
  });

  it('links a lease to its tenant’s ledger, and leaves a target it cannot find unlinked', async () => {
    serve(RENT_RUN);
    open('run-1');

    expect(await screen.findByRole('link', { name: 'Bob Full' })).toHaveAttribute(
      'href',
      '/tenants/tenant-bob',
    );
    const missing = rows()[1]!;
    expect(within(missing).getByText('Lease not found')).toBeVisible();
    expect(within(missing).queryByRole('link')).toBeNull();
  });

  it('links an owner to the owner’s page', async () => {
    serve(DISBURSEMENT_RUN);
    open('run-2');

    expect(await screen.findByRole('link', { name: 'Stillwater Properties' })).toHaveAttribute(
      'href',
      '/owners/owner-still',
    );
    expect(within(rows()[0]!).getByText('Below reserve floor')).toBeVisible();
    const missing = rows()[1]!;
    expect(
      within(within(missing).getAllByRole('cell')[0]!).getByText('Owner not found'),
    ).toBeVisible();
    expect(within(missing).queryByRole('link')).toBeNull();
  });

  it('counts the targets the page lists, and states the run’s total', async () => {
    serve(RENT_RUN);
    open('run-1');

    const summary = await screen.findByRole('group', { name: 'Run summary' });
    expect(summary).toHaveTextContent('1 posted');
    expect(summary).toHaveTextContent('1 skipped');
    expect(summary).toHaveTextContent('2 excluded');
    expect(summary).toHaveTextContent('$1,380.00 total');
  });

  it('narrows the list to one outcome and back', async () => {
    serve(RENT_RUN);
    open('run-1');
    await screen.findByText('Bob Full');
    expect(
      within(screen.getByRole('group', { name: 'Show targets by outcome' }))
        .getAllByRole('button')
        .map((b) => b.textContent),
    ).toEqual(['All (4)', 'Posted (1)', 'Skipped (1)', 'Excluded (2)']);

    await userEvent.click(screen.getByRole('button', { name: 'Skipped (1)' }));
    expect(rows().map((r) => r.textContent)).toEqual([expect.stringContaining('Alice Full')]);
    expect(screen.getByRole('button', { name: 'Skipped (1)' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );

    await userEvent.click(screen.getByRole('button', { name: 'Excluded (2)' }));
    expect(rows()).toHaveLength(2);

    await userEvent.click(screen.getByRole('button', { name: 'All (4)' }));
    expect(rows()).toHaveLength(4);
    expect(screen.getByRole('button', { name: 'Posted (1)' })).toHaveAttribute(
      'aria-pressed',
      'false',
    );
  });

  it('warns when the run’s postings land in a month whose owner statement was already issued', async () => {
    serve(RENT_RUN);
    open('run-1');

    expect(
      await screen.findByText(
        /Harborview Holdings — the accrual statement for March 2026 was already issued/,
      ),
    ).toBeVisible();
  });

  it('does not check issued statements for a run that posted nothing', async () => {
    const coverage = serve(DISBURSEMENT_RUN);
    open('run-2');

    await screen.findByText('Stillwater Properties');
    // The notice mounts its status region with the page, before any read it makes: asserting the
    // region is absent needs no wait, unlike counting requests, which only arrive later.
    expect(screen.queryByRole('status')).toBeNull();
    expect(coverage.calls).toBe(0);
  });

  it('goes back to run history', async () => {
    serve(RENT_RUN);
    open('run-1');

    await userEvent.click(await screen.findByRole('button', { name: 'Run history' }));
    expect(await screen.findByText('Operations page at /operations?tab=history')).toBeVisible();
  });

  it('says so when no target has the outcome chosen', async () => {
    serve(DISBURSEMENT_RUN);
    open('run-2');
    await screen.findByText('Stillwater Properties');

    await userEvent.click(screen.getByRole('button', { name: 'Posted (0)' }));

    expect(screen.getByText('No posted targets in this run')).toBeVisible();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('names a lease whose tenant it cannot link to without linking it', async () => {
    serve({
      ...RENT_RUN,
      items: [
        item({
          id: 'i9',
          outcome: 'Posted',
          amount: 900,
          targetName: 'Dana Orphan',
          unitLabel: '#D',
        }),
      ],
    });
    open('run-1');

    const row = (await screen.findByText('Dana Orphan')).closest('tr')!;
    expect(row).toHaveTextContent('#D');
    expect(within(row).queryByRole('link')).toBeNull();
  });

  it('lets the issued-statement warning be dismissed', async () => {
    serve(RENT_RUN);
    open('run-1');

    const warning = await screen.findByText(/Harborview Holdings — the accrual statement/);
    await userEvent.click(screen.getByRole('button', { name: /dismiss/i }));
    expect(warning).not.toBeInTheDocument();
  });
});

describe('RunDetailPage — when the run cannot be shown', () => {
  it('says a run that does not exist was not found', async () => {
    serve(RENT_RUN);
    open('run-gone');

    expect(await screen.findByText('Run not found')).toBeVisible();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('carries the support reference and offers a retry when the read fails', async () => {
    let attempt = 0;
    server.use(
      http.get('/api/operations/runs/:id', () => {
        attempt += 1;
        return attempt === 1
          ? HttpResponse.json(
              { detail: 'The run log is rebuilding.', correlationId: REFERENCE },
              { status: 503 },
            )
          : HttpResponse.json(DISBURSEMENT_RUN);
      }),
    );
    open('run-2');

    expect(await screen.findByText('The run log is rebuilding.')).toBeVisible();
    expect(screen.getByText(`Reference: ${REFERENCE}`)).toBeVisible();
    expect(screen.queryByText('Run not found')).toBeNull();

    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText('Stillwater Properties')).toBeVisible();
  });

  it('says so when a run recorded no targets', async () => {
    serve({ ...DISBURSEMENT_RUN, items: [] });
    open('run-2');

    expect(await screen.findByText('This run recorded no targets')).toBeVisible();
  });

  it('shows a loading state, not an empty one, while the run is read', async () => {
    server.use(http.get('/api/operations/runs/:id', () => new Promise(() => {})));
    open('run-2');

    expect(await screen.findByLabelText('Loading the run')).toBeVisible();
    expect(screen.queryByText('This run recorded no targets')).toBeNull();
  });
});
