import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { RefundCheckFund, RefundCheckView, TenantDetail } from '@/api';
import { trackInteraction } from '@/lib/telemetry';
import { server } from '@/test/mocks/server';
import { RefundCheckModal } from './RefundCheckModal';

vi.mock('@/lib/telemetry', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/telemetry')>()),
  trackInteraction: vi.fn(),
}));

const TENANT: TenantDetail = {
  id: 't1',
  displayName: 'Jasmine Carter',
  contact: { email: null, phone: null },
  lifecycleStatus: 'past',
  lease: null,
  unitLabel: '#2B',
  propertyAddress: '412 Oakmont Ave',
  ownerId: 'o1',
  ownerName: 'Hargrove Family Trust',
  balance: 0,
  depositHeld: 1450,
  financialStanding: { delinquentBalance: 0, unappliedCredit: 0 },
};

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
    name: 'Security Deposit Trust',
    institution: null,
    mask: null,
    purpose: 'deposit',
    isActive: true,
  },
];

const DEPOSIT: RefundCheckFund = {
  source: 'deposit',
  bankAccountId: 'dep1',
  propertyId: null,
  ownerId: 'o1',
  held: 1450,
  nextCheckNumber: 1043,
};
const PREPAID: RefundCheckFund = {
  source: 'prepayment',
  bankAccountId: 'trust1',
  propertyId: null,
  ownerId: null,
  held: 75.5,
  nextCheckNumber: null,
};

const ISSUED: RefundCheckView = {
  id: 'chk1',
  tenantId: 't1',
  bankAccountId: 'dep1',
  checkNumber: 1043,
  source: 'deposit',
  amount: 1450,
  issueDate: '2026-10-01',
  payeeName: 'Jasmine Carter',
  addressLine1: '412 Oakmont Ave',
  addressLine2: '#2B',
  city: 'Asheville',
  state: 'NC',
  postalCode: '28801',
  memo: null,
  entryId: 'entry-refund',
  status: 'outstanding',
  voidEntryId: null,
  printCount: 0,
  lastPrintedAt: null,
  createdAt: '2026-10-01T12:00:00Z',
};

const OWNERS = {
  items: [{ id: 'o1', name: 'Hargrove Family Trust' }],
  total: 1,
  page: 1,
  pageSize: 200,
};

function readsFor(funds: RefundCheckFund[]) {
  return [
    http.get('/api/refund-checks/options', () => HttpResponse.json({ funds })),
    http.get('/api/settings/banks', () => HttpResponse.json(BANKS)),
    http.get('/api/directory/owners', () => HttpResponse.json(OWNERS)),
  ];
}

function renderModal() {
  const onIssued = vi.fn();
  const onClose = vi.fn();
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  render(
    <QueryClientProvider client={queryClient}>
      <RefundCheckModal tenantId="t1" tenant={TENANT} onClose={onClose} onIssued={onIssued} />
    </QueryClientProvider>,
  );
  return { onIssued, onClose };
}

/** The address the tenant record cannot supply (it carries the street line only). */
async function completeAddress() {
  await userEvent.type(screen.getByLabelText('City'), 'Asheville');
  await userEvent.type(screen.getByLabelText('State'), 'nc');
  await userEvent.type(screen.getByLabelText('ZIP'), '28801');
}

// Captured so assertions read plain mocks rather than unbound methods off `URL`.
const createObjectURL = vi.fn((_blob: Blob | MediaSource) => 'blob:test');
const revokeObjectURL = vi.fn((_url: string) => {});

beforeEach(() => {
  document.body.innerHTML = '';
  vi.clearAllMocks();
  globalThis.URL.createObjectURL = createObjectURL;
  globalThis.URL.revokeObjectURL = revokeObjectURL;
});

describe('RefundCheckModal prefill', () => {
  it('prefills a single fund read-only with its held amount, next number, payee and unit address', async () => {
    server.use(...readsFor([DEPOSIT]));
    renderModal();

    expect(await screen.findByLabelText('Amount')).toHaveValue('1450.00');
    expect(screen.getByLabelText('Check number')).toHaveValue('1043');
    expect(screen.getByLabelText('Pay to the order of')).toHaveValue('Jasmine Carter');
    expect(screen.getByLabelText('Mailing address')).toHaveValue('412 Oakmont Ave');
    expect(screen.getByLabelText('Address line 2')).toHaveValue('#2B');
    // One fund: shown, not offered as a choice.
    expect(screen.queryByRole('radio')).toBeNull();
    expect(screen.getByText('Security deposit')).toBeInTheDocument();
    expect(await screen.findByText('Security Deposit Trust')).toBeInTheDocument();
    expect(screen.getByText('$1,450.00')).toBeInTheDocument();
  });

  it('offers several funds as a labelled radio group and prefills from the one chosen', async () => {
    server.use(...readsFor([DEPOSIT, PREPAID]));
    renderModal();

    const group = await screen.findByRole('group', { name: 'Refund from' });
    const radios = within(group).getAllByRole('radio');
    expect(radios).toHaveLength(2);
    expect(within(group).getByText('Security deposit')).toBeInTheDocument();
    expect(within(group).getByText('Prepaid credit')).toBeInTheDocument();
    expect(await within(group).findByText(/Operating Trust/)).toBeInTheDocument();
    // Owner names tell deposit buckets apart.
    expect(await within(group).findByText(/Hargrove Family Trust/)).toBeInTheDocument();

    // Nothing is preselected: the operator chooses which bank's stock the check is printed on.
    await userEvent.click(screen.getByRole('button', { name: 'Issue check' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Choose which fund to refund from.');

    await userEvent.click(within(group).getByRole('radio', { name: /Prepaid credit/ }));
    expect(screen.getByLabelText('Amount')).toHaveValue('75.50');
    // No refund check on that bank yet: the number is the operator's to enter, and the field says so.
    const number = screen.getByLabelText('Check number');
    expect(number).toHaveValue('');
    expect(number).toHaveAccessibleDescription(/First check on this account/);
  });

  it('explains when nothing is held, instead of offering an empty form', async () => {
    server.use(...readsFor([]));
    renderModal();

    expect(
      await screen.findByText('No held deposit or prepaid credit to refund.'),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Issue check' })).toBeNull();
  });
});

describe('RefundCheckModal blocks the write while a read it depends on is unavailable', () => {
  it('disables Issue while the funds are still loading', () => {
    server.use(
      http.get('/api/refund-checks/options', () => new Promise<never>(() => {})),
      http.get('/api/settings/banks', () => HttpResponse.json(BANKS)),
    );
    renderModal();

    expect(screen.getByText('Loading the held funds…')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Issue check' })).toBeDisabled();
  });

  it('reports a failed funds read with the server’s message and reference, and blocks Issue', async () => {
    let posted = false;
    server.use(
      http.get('/api/refund-checks/options', () =>
        HttpResponse.json(
          {
            title: 'Service Unavailable',
            detail: 'The ledger is temporarily unavailable.',
            correlationId: 'abcabcabcabcabcabcabcabcabcabc12',
          },
          { status: 503 },
        ),
      ),
      http.get('/api/settings/banks', () => HttpResponse.json(BANKS)),
      http.post('/api/refund-checks', () => {
        posted = true;
        return HttpResponse.json(ISSUED);
      }),
    );
    renderModal();

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('The ledger is temporarily unavailable.');
    expect(alert).toHaveTextContent('Reference: abcabcabcabcabcabcabcabcabcabc12');
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Issue check' })).toBeDisabled();
    // Never rendered as a confirmed "nothing to refund".
    expect(screen.queryByText(/No held deposit/)).toBeNull();
    expect(posted).toBe(false);
  });

  it('blocks Issue when the bank names fail — the operator must know which stock to load', async () => {
    server.use(
      http.get('/api/refund-checks/options', () => HttpResponse.json({ funds: [DEPOSIT] })),
      http.get('/api/settings/banks', () =>
        HttpResponse.json(
          { detail: 'Bank list offline.', correlationId: 'feedfeedfeedfeedfeedfeedfeedfeed' },
          { status: 500 },
        ),
      ),
    );
    renderModal();

    expect(await screen.findByText('Bank list offline.')).toBeInTheDocument();
    expect(screen.getByText('Reference: feedfeedfeedfeedfeedfeedfeedfeed')).toBeInTheDocument();
    expect(screen.getByText('Account name unavailable')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Issue check' })).toBeDisabled();
  });
});

describe('RefundCheckModal server rejections', () => {
  it('says a check number is taken, naming the number and the account', async () => {
    server.use(
      ...readsFor([DEPOSIT]),
      http.post('/api/refund-checks', () =>
        HttpResponse.json(
          { code: 'check_number_taken', detail: 'That check number has already been used.' },
          { status: 409 },
        ),
      ),
    );
    const { onIssued } = renderModal();
    await screen.findByText('Security Deposit Trust');
    await completeAddress();
    await userEvent.click(screen.getByRole('button', { name: 'Issue check' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Check #1043 has already been used on Security Deposit Trust. Enter the number printed on the next blank check in the printer.',
    );
    expect(screen.getByLabelText('Check number')).toHaveAttribute('aria-invalid', 'true');
    expect(onIssued).not.toHaveBeenCalled();
  });

  it('says the amount exceeds what the fund holds now', async () => {
    server.use(
      ...readsFor([DEPOSIT]),
      http.post('/api/refund-checks', () =>
        HttpResponse.json(
          { code: 'insufficient_liability', detail: 'Refund exceeds the held amount.' },
          { status: 409 },
        ),
      ),
    );
    renderModal();
    await screen.findByText('Security Deposit Trust');
    await completeAddress();
    await userEvent.click(screen.getByRole('button', { name: 'Issue check' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      /more than this fund holds now.*lower the amount/,
    );
    expect(screen.getByLabelText('Amount')).toHaveAttribute('aria-invalid', 'true');
  });

  it('refuses an amount above the held balance before posting anything', async () => {
    let posted = false;
    server.use(
      ...readsFor([DEPOSIT]),
      http.post('/api/refund-checks', () => {
        posted = true;
        return HttpResponse.json(ISSUED);
      }),
    );
    renderModal();
    const amount = await screen.findByLabelText('Amount');
    await userEvent.clear(amount);
    await userEvent.type(amount, '1450.01');
    await completeAddress();
    await userEvent.click(screen.getByRole('button', { name: 'Issue check' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('more than this fund holds');
    expect(posted).toBe(false);
  });
});

describe('RefundCheckModal issue → print', () => {
  it('issues once with the chosen bucket, then offers Print and Done but never Issue again', async () => {
    const bodies: Record<string, unknown>[] = [];
    let printed = 0;
    server.use(
      ...readsFor([DEPOSIT]),
      http.post('/api/refund-checks', async ({ request }) => {
        bodies.push((await request.json()) as Record<string, unknown>);
        return HttpResponse.json(ISSUED);
      }),
      http.post('/api/refund-checks/:id/pdf', () => {
        printed += 1;
        return new HttpResponse(new Blob(['%PDF-1.7']), {
          headers: { 'Content-Type': 'application/pdf' },
        });
      }),
    );
    const { onIssued } = renderModal();
    await screen.findByText('Security Deposit Trust');
    await completeAddress();
    await userEvent.click(screen.getByRole('button', { name: 'Issue check' }));

    expect(await screen.findByRole('dialog', { name: 'Refund check issued' })).toBeInTheDocument();
    expect(onIssued).toHaveBeenCalledWith(ISSUED);
    expect(bodies).toHaveLength(1);
    expect(bodies[0]).toMatchObject({
      tenantId: 't1',
      source: 'deposit',
      amount: 1450,
      checkNumber: 1043,
      payeeName: 'Jasmine Carter',
      addressLine1: '412 Oakmont Ave',
      addressLine2: '#2B',
      city: 'Asheville',
      state: 'NC',
      postalCode: '28801',
      bucket: { bankAccountId: 'dep1', propertyId: null, ownerId: 'o1' },
    });
    expect(bodies[0]?.key).toMatch(/^[0-9a-f-]{36}$/);
    expect(screen.queryByRole('button', { name: /Issue check/ })).toBeNull();
    // Focus moves to the next step rather than falling out of the dialog with the removed form.
    expect(screen.getByRole('button', { name: 'Print check' })).toHaveFocus();

    await userEvent.click(screen.getByRole('button', { name: 'Print check' }));
    expect(await screen.findByText('Sent to the browser as a PDF.')).toBeInTheDocument();
    expect(printed).toBe(1);
    expect(createObjectURL).toHaveBeenCalledTimes(1);
    expect(revokeObjectURL).toHaveBeenCalledTimes(1);
    // Open (1) + Issue (2) + Print (3): inside the ≤ 4 budget.
    expect(vi.mocked(trackInteraction).mock.calls).toEqual([['issue-refund-check', 3, true]]);

    // A reprint keeps the same check and does not report the budget twice.
    await userEvent.click(screen.getByRole('button', { name: 'Print again' }));
    await vi.waitFor(() => expect(printed).toBe(2));
    expect(trackInteraction).toHaveBeenCalledTimes(1);
  });

  it('counts a fund choice toward the budget, and a retry reuses the same key', async () => {
    const keys: unknown[] = [];
    let attempt = 0;
    server.use(
      ...readsFor([DEPOSIT, PREPAID]),
      http.post('/api/refund-checks', async ({ request }) => {
        keys.push(((await request.json()) as { key: unknown }).key);
        attempt += 1;
        return attempt === 1
          ? HttpResponse.json({ detail: 'Gateway timeout.' }, { status: 504 })
          : HttpResponse.json(ISSUED);
      }),
      http.post(
        '/api/refund-checks/:id/pdf',
        () =>
          new HttpResponse(new Blob(['%PDF-1.7']), {
            headers: { 'Content-Type': 'application/pdf' },
          }),
      ),
    );
    renderModal();
    const group = await screen.findByRole('group', { name: 'Refund from' });
    await userEvent.click(within(group).getByRole('radio', { name: /Security deposit/ }));
    await completeAddress();
    await userEvent.click(screen.getByRole('button', { name: 'Issue check' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Gateway timeout.');
    await userEvent.click(screen.getByRole('button', { name: 'Issue check' }));
    await screen.findByRole('dialog', { name: 'Refund check issued' });
    expect(keys).toHaveLength(2);
    expect(keys[0]).toBe(keys[1]);

    await userEvent.click(screen.getByRole('button', { name: 'Print check' }));
    await screen.findByText('Sent to the browser as a PDF.');
    // Open (1) + fund choice (2) + Issue (3) + Print (4) = the budget exactly.
    expect(vi.mocked(trackInteraction).mock.calls).toEqual([['issue-refund-check', 4, true]]);
  });

  it('keeps the check issued and says so when the PDF cannot be produced', async () => {
    server.use(
      ...readsFor([DEPOSIT]),
      http.post('/api/refund-checks', () => HttpResponse.json(ISSUED)),
      http.post('/api/refund-checks/:id/pdf', () =>
        HttpResponse.json(
          { detail: 'Renderer unavailable.', correlationId: '0123456789abcdef0123456789abcdef' },
          { status: 503 },
        ),
      ),
    );
    renderModal();
    await screen.findByText('Security Deposit Trust');
    await completeAddress();
    await userEvent.click(screen.getByRole('button', { name: 'Issue check' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Print check' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Renderer unavailable.');
    expect(alert).toHaveTextContent('Reference: 0123456789abcdef0123456789abcdef');
    expect(trackInteraction).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Print check' })).toBeEnabled();
  });
});
