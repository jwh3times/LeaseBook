import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { RefundCheckPage, RefundCheckView } from '@/api';
import { server } from '@/test/mocks/server';
import { RefundChecksPanel } from './RefundChecksPanel';

function check(overrides: Partial<RefundCheckView>): RefundCheckView {
  return {
    id: 'c1',
    tenantId: 't1',
    bankAccountId: 'dep1',
    checkNumber: 1043,
    source: 'deposit',
    amount: 1450,
    issueDate: '2026-10-01',
    payeeName: 'Jasmine Carter',
    addressLine1: '412 Oakmont Ave',
    addressLine2: null,
    city: 'Asheville',
    state: 'NC',
    postalCode: '28801',
    memo: null,
    entryId: 'e1',
    status: 'outstanding',
    voidEntryId: null,
    printCount: 0,
    lastPrintedAt: null,
    createdAt: '2026-10-01T12:00:00Z',
    ...overrides,
  };
}

const CHECKS = [
  check({ id: 'c4', checkNumber: 1046, status: 'outstanding', printCount: 2 }),
  check({
    id: 'c3',
    checkNumber: 1045,
    status: 'cleared',
    printCount: 1,
    payeeName: 'Devon Pryor',
  }),
  check({ id: 'c2', checkNumber: 1044, status: 'reconciled', printCount: 1, source: 'prepayment' }),
  check({ id: 'c1', checkNumber: 1043, status: 'voided', voidEntryId: 'rev1', printCount: 1 }),
];

/** One page of the list as the server sends it; `total` defaults to a single complete page. */
function pageOf(
  items: RefundCheckView[],
  { total = items.length, page = 1, pageSize = 50 } = {},
): RefundCheckPage {
  return { items, total, page, pageSize };
}

function renderPanel() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  render(
    <QueryClientProvider client={queryClient}>
      <RefundChecksPanel bankAccountId="dep1" bankName="Security Deposit Trust" />
    </QueryClientProvider>,
  );
}

const rowFor = (number: number) =>
  screen.getAllByRole('row').find((row) => within(row).queryByText(String(number)))!;

// Captured so assertions read plain mocks rather than unbound methods off `URL`.
const createObjectURL = vi.fn((_blob: Blob | MediaSource) => 'blob:test');
const revokeObjectURL = vi.fn((_url: string) => {});

beforeEach(() => {
  document.body.innerHTML = '';
  vi.clearAllMocks();
  globalThis.URL.createObjectURL = createObjectURL;
  globalThis.URL.revokeObjectURL = revokeObjectURL;
});

describe('RefundChecksPanel', () => {
  it('labels every status in words with its own icon, and says why an action is unavailable', async () => {
    server.use(http.get('/api/refund-checks', () => HttpResponse.json(pageOf(CHECKS))));
    renderPanel();

    await screen.findByRole('table', { name: 'Refund checks on Security Deposit Trust' });
    expect(screen.getByText('4 checks · Security Deposit Trust')).toBeInTheDocument();

    const outstanding = rowFor(1046);
    expect(within(outstanding).getByText('Outstanding')).toBeInTheDocument();
    expect(within(outstanding).getByText('2×')).toBeInTheDocument();
    expect(within(outstanding).getByRole('button', { name: 'Reprint check #1046' })).toBeEnabled();
    expect(within(outstanding).getByRole('button', { name: 'Void check #1046' })).toBeEnabled();

    for (const [number, label] of [
      [1045, 'Cleared'],
      [1044, 'Reconciled'],
    ] as const) {
      const row = rowFor(number);
      expect(within(row).getByText(label)).toBeInTheDocument();
      const voidButton = within(row).getByRole('button', { name: `Void check #${number}` });
      expect(voidButton).toBeDisabled();
      expect(voidButton).toHaveAccessibleDescription('Cleared the bank — can’t be voided.');
      // Cleared checks can still be reprinted (a lost copy); only void is closed.
      expect(within(row).getByRole('button', { name: `Reprint check #${number}` })).toBeEnabled();
    }

    const voided = rowFor(1043);
    expect(within(voided).getByText('Voided')).toBeInTheDocument();
    const reprint = within(voided).getByRole('button', { name: 'Reprint check #1043' });
    expect(reprint).toBeDisabled();
    expect(reprint).toHaveAccessibleDescription('Voided — can’t be printed or voided again.');
    expect(within(voided).getByRole('button', { name: 'Void check #1043' })).toBeDisabled();

    // Status is carried by an icon + word, never the tone class alone.
    for (const badge of document.querySelectorAll('.pf-badge')) {
      expect(badge.querySelector('svg')).not.toBeNull();
      expect(badge.textContent?.trim()).not.toBe('');
    }
  });

  it('shows an empty state only for a read that succeeded with no checks', async () => {
    server.use(http.get('/api/refund-checks', () => HttpResponse.json(pageOf([]))));
    renderPanel();
    expect(await screen.findByText('No refund checks on this account')).toBeInTheDocument();
  });

  it('reports a failed list read with the server’s message and reference', async () => {
    server.use(
      http.get('/api/refund-checks', () =>
        HttpResponse.json(
          { detail: 'Checks are unavailable.', correlationId: 'c0ffeec0ffeec0ffeec0ffeec0ffee00' },
          { status: 500 },
        ),
      ),
    );
    renderPanel();

    expect(await screen.findByText('Checks are unavailable.')).toBeInTheDocument();
    expect(screen.getByText('Reference: c0ffeec0ffeec0ffeec0ffeec0ffee00')).toBeInTheDocument();
    expect(screen.getByText('Count unavailable')).toBeInTheDocument();
    expect(screen.queryByText('No refund checks on this account')).toBeNull();
  });

  it('prints a check as a PDF download and refreshes the print count', async () => {
    let listReads = 0;
    let printed: string | undefined;
    server.use(
      http.get('/api/refund-checks', () => {
        listReads += 1;
        return HttpResponse.json(pageOf(CHECKS));
      }),
      http.post('/api/refund-checks/:id/pdf', ({ params }) => {
        printed = params.id as string;
        return new HttpResponse(new Blob(['%PDF-1.7']), {
          headers: { 'Content-Type': 'application/pdf' },
        });
      }),
    );
    renderPanel();

    await userEvent.click(await screen.findByRole('button', { name: 'Reprint check #1046' }));
    await vi.waitFor(() => expect(listReads).toBe(2));
    expect(printed).toBe('c4');
    expect(createObjectURL).toHaveBeenCalledTimes(1);
    expect(revokeObjectURL).toHaveBeenCalledTimes(1);
  });

  it('voids with a required reason and reports a check that cleared meanwhile', async () => {
    let body: unknown;
    server.use(
      http.get('/api/refund-checks', () => HttpResponse.json(pageOf(CHECKS))),
      http.post('/api/refund-checks/:id/void', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json(
          { code: 'refund_check_cleared', detail: 'This check has cleared the bank.' },
          { status: 409 },
        );
      }),
    );
    renderPanel();

    await userEvent.click(await screen.findByRole('button', { name: 'Void check #1046' }));
    const dialog = screen.getByRole('dialog', { name: 'Void check #1046' });
    await userEvent.click(within(dialog).getByRole('button', { name: 'Void check' }));
    expect(within(dialog).getByRole('alert')).toHaveTextContent(
      'A reason is required to void a check.',
    );
    expect(body).toBeUndefined();

    await userEvent.type(within(dialog).getByLabelText('Reason (internal note)'), 'misprint');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Void check' }));
    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'Check #1046 has cleared the bank, so it can no longer be voided.',
    );
    expect(body).toEqual({ reason: 'misprint' });
  });

  it('closes the void dialog and refreshes the list once voided', async () => {
    let voided = false;
    server.use(
      http.get('/api/refund-checks', () =>
        HttpResponse.json(
          pageOf(
            voided
              ? CHECKS.map((c) =>
                  c.id === 'c4' ? { ...c, status: 'voided', voidEntryId: 'rev' } : c,
                )
              : CHECKS,
          ),
        ),
      ),
      http.post('/api/refund-checks/:id/void', () => {
        voided = true;
        return HttpResponse.json({ ...CHECKS[0], status: 'voided', voidEntryId: 'rev' });
      }),
    );
    renderPanel();

    await userEvent.click(await screen.findByRole('button', { name: 'Void check #1046' }));
    const dialog = screen.getByRole('dialog', { name: 'Void check #1046' });
    await userEvent.type(
      within(dialog).getByLabelText('Reason (internal note)'),
      'misprint{Enter}',
    );

    await vi.waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await vi.waitFor(() => expect(within(rowFor(1046)).getByText('Voided')).toBeInTheDocument());
  });

  it('pages through every check on the server and counts all of them, not just the page', async () => {
    const requested: { page: string | null; status: string | null }[] = [];
    server.use(
      http.get('/api/refund-checks', ({ request }) => {
        const query = new URL(request.url).searchParams;
        requested.push({ page: query.get('page'), status: query.get('status') });
        const page = Number(query.get('page') ?? '1');
        return HttpResponse.json(
          pageOf([check({ id: `p${page}`, checkNumber: 2000 + page })], { total: 120, page }),
        );
      }),
    );
    renderPanel();

    expect(await screen.findByText('120 checks · Security Deposit Trust')).toBeInTheDocument();
    expect(screen.getByText('1–50 of 120')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Previous page' })).toHaveAttribute(
      'aria-disabled',
      'true',
    );

    await userEvent.click(screen.getByRole('button', { name: 'Next page' }));
    expect(await screen.findByText('51–100 of 120')).toBeInTheDocument();
    expect(rowFor(2002)).toBeDefined();

    await userEvent.click(screen.getByRole('button', { name: 'Next page' }));
    expect(await screen.findByText('101–120 of 120')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next page' })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
    expect(requested.map((r) => r.page)).toEqual(['1', '2', '3']);
    expect(requested.every((r) => r.status === null)).toBe(true);
  });

  it('filters to outstanding checks on the server and starts again from the first page', async () => {
    const requested: { page: string | null; status: string | null }[] = [];
    server.use(
      http.get('/api/refund-checks', ({ request }) => {
        const query = new URL(request.url).searchParams;
        const status = query.get('status');
        const page = Number(query.get('page') ?? '1');
        requested.push({ page: query.get('page'), status });
        return HttpResponse.json(
          status === 'outstanding'
            ? pageOf([check({ id: 'o1', checkNumber: 1001 })], { total: 3 })
            : pageOf([check({ id: `p${page}`, checkNumber: 2000 + page })], { total: 120, page }),
        );
      }),
    );
    renderPanel();

    const all = await screen.findByRole('button', { name: 'All' });
    expect(all).toHaveAttribute('aria-pressed', 'true');
    await userEvent.click(await screen.findByRole('button', { name: 'Next page' }));
    await screen.findByText('51–100 of 120');

    await userEvent.click(screen.getByRole('button', { name: 'Outstanding' }));
    expect(await screen.findByText('3 outstanding · Security Deposit Trust')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Outstanding' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(rowFor(1001)).toBeDefined();
    // Everything fits on one page, so there is nothing to page through.
    expect(screen.queryByRole('button', { name: 'Next page' })).toBeNull();
    expect(requested.at(-1)).toEqual({ page: '1', status: 'outstanding' });
  });

  it('says there is nothing outstanding rather than nothing at all when the filter is empty', async () => {
    server.use(
      http.get('/api/refund-checks', ({ request }) =>
        HttpResponse.json(
          new URL(request.url).searchParams.get('status') === 'outstanding'
            ? pageOf([])
            : pageOf(CHECKS),
        ),
      ),
    );
    renderPanel();

    await userEvent.click(await screen.findByRole('button', { name: 'Outstanding' }));
    expect(
      await screen.findByText('No outstanding refund checks on this account'),
    ).toBeInTheDocument();
    expect(screen.getByText('0 outstanding · Security Deposit Trust')).toBeInTheDocument();
  });

  it('steps back a page when the page it is on empties, instead of claiming there are no checks', async () => {
    let total = 51;
    server.use(
      http.get('/api/refund-checks', ({ request }) => {
        const page = Number(new URL(request.url).searchParams.get('page') ?? '1');
        if (page === 1) return HttpResponse.json(pageOf([check({ id: 'first' })], { total }));
        return HttpResponse.json(
          total > 50
            ? pageOf([check({ id: 'last', checkNumber: 1099 })], { total, page })
            : pageOf([], { total, page }),
        );
      }),
      http.post('/api/refund-checks/:id/pdf', () => {
        // Meanwhile another check was voided elsewhere: the refreshed list is one shorter.
        total = 50;
        return new HttpResponse(new Blob(['%PDF-1.7']), {
          headers: { 'Content-Type': 'application/pdf' },
        });
      }),
    );
    renderPanel();

    await userEvent.click(await screen.findByRole('button', { name: 'Next page' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Print check #1099' }));

    expect(await screen.findByText('50 checks · Security Deposit Trust')).toBeInTheDocument();
    await vi.waitFor(() => expect(rowFor(1043)).toBeDefined());
    expect(screen.queryByText('No refund checks on this account')).toBeNull();
  });

  it('keeps keyboard focus on the pager while the next page loads and at the last page', async () => {
    server.use(
      http.get('/api/refund-checks', async ({ request }) => {
        const page = Number(new URL(request.url).searchParams.get('page') ?? '1');
        await new Promise((resolve) => setTimeout(resolve, 20));
        return HttpResponse.json(
          pageOf([check({ id: `p${page}`, checkNumber: 2000 + page })], { total: 60, page }),
        );
      }),
    );
    renderPanel();

    const next = await screen.findByRole('button', { name: 'Next page' });
    next.focus();
    await userEvent.keyboard('{Enter}');
    expect(await screen.findByText('51–60 of 60')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next page' })).toHaveFocus();
    expect(screen.getByRole('button', { name: 'Next page' })).toHaveAttribute(
      'aria-disabled',
      'true',
    );

    // At the end, Enter again changes nothing.
    await userEvent.keyboard('{Enter}');
    expect(screen.getByText('51–60 of 60')).toBeInTheDocument();
  });
});
