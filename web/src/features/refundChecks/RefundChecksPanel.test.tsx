import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { RefundCheckView } from '@/api';
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
    server.use(http.get('/api/refund-checks', () => HttpResponse.json(CHECKS)));
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
    server.use(http.get('/api/refund-checks', () => HttpResponse.json([])));
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
        return HttpResponse.json(CHECKS);
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
      http.get('/api/refund-checks', () => HttpResponse.json(CHECKS)),
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
          voided
            ? CHECKS.map((c) =>
                c.id === 'c4' ? { ...c, status: 'voided', voidEntryId: 'rev' } : c,
              )
            : CHECKS,
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
});
