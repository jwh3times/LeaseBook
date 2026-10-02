import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import { ApplyModal } from './ApplyModal';

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
    name: 'Deposit Trust',
    institution: null,
    mask: null,
    purpose: 'deposit',
    isActive: true,
  },
];

/** Prepaid credit split across both trust banks, beside a held deposit that must not be offered. */
const SPLIT_FUNDS = [
  {
    source: 'deposit',
    bankAccountId: 'dep1',
    propertyId: 'p',
    ownerId: 'o',
    held: 1450,
    nextCheckNumber: 1,
  },
  {
    source: 'prepayment',
    bankAccountId: 'trust1',
    propertyId: null,
    ownerId: null,
    held: 100,
    nextCheckNumber: 1,
  },
  {
    source: 'prepayment',
    bankAccountId: 'dep1',
    propertyId: null,
    ownerId: null,
    held: 200,
    nextCheckNumber: 1,
  },
];

/** The server's #475 behavior: refuse an unnamed bank while the credit spans banks; post a named one. */
function ambiguousUnlessNamed(bodies: Record<string, unknown>[]) {
  return http.post(
    '/api/accounting/tenants/:tenantId/prepayment-applications',
    async ({ request }) => {
      const body = (await request.json()) as Record<string, unknown>;
      bodies.push(body);
      return body.bankAccountId === null
        ? HttpResponse.json(
            {
              code: 'prepayment_bank_ambiguous',
              detail:
                "This tenant's prepaid credit sits in more than one account. Choose which one to apply from.",
            },
            { status: 409 },
          )
        : HttpResponse.json({ entryId: 'p2' });
    },
  );
}

async function askForPrepayment(amount: string) {
  await screen.findByText(/Operating Trust/);
  await userEvent.selectOptions(screen.getByLabelText('Source'), 'prepayment');
  await userEvent.type(screen.getByLabelText('Amount'), amount);
  await userEvent.click(screen.getByRole('button', { name: 'Apply' }));
}

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
    const { onApplied } = renderModal();

    await screen.findByText(/Operating Trust/);
    await userEvent.type(screen.getByLabelText('Amount'), '100');
    await userEvent.click(screen.getByRole('button', { name: 'Apply' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/reconciled and locked/i);
    expect(onApplied).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Amount')).toBeInTheDocument();
  });
});

describe('ApplyModal when the trust banks cannot be read', () => {
  it('reports the read failure rather than missing configuration', async () => {
    let posted = false;
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/settings/banks', () => new HttpResponse(null, { status: 503 })),
      http.post('/api/accounting/tenants/:tenantId/deposit-applications', () => {
        posted = true;
        return HttpResponse.json({ entryId: 'e1' });
      }),
    );
    renderModal();

    expect(await screen.findByRole('alert')).toBeInTheDocument();

    // "No trust bank is configured" would send the operator to Settings to fix a bank that is
    // already there. The org's configuration is unknown, not known to be empty.
    expect(screen.queryByText(/no trust bank is configured/i)).toBeNull();

    // Money must not move against a bank resolved from an unread list, by either route in.
    await userEvent.type(screen.getByLabelText(/amount/i), '100');
    expect(screen.getByRole('button', { name: /^apply$/i })).toBeDisabled();
    await userEvent.keyboard('{Enter}');
    expect(posted).toBe(false);
  });

  it('still reports genuine missing configuration on a successful empty list', async () => {
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/settings/banks', () => HttpResponse.json([])),
    );
    renderModal();

    await userEvent.type(await screen.findByLabelText(/amount/i), '100');
    await userEvent.keyboard('{Enter}');

    expect(await screen.findByText(/no trust bank is configured/i)).toBeInTheDocument();
  });

  it('recovers once the banks read succeeds', async () => {
    let attempt = 0;
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/settings/banks', () => {
        attempt += 1;
        return attempt === 1 ? new HttpResponse(null, { status: 503 }) : HttpResponse.json(BANKS);
      }),
    );
    renderModal();

    await userEvent.click(await screen.findByRole('button', { name: /retry/i }));

    expect(await screen.findByRole('button', { name: /^apply$/i })).toBeEnabled();
  });
});

// #468 (ADR-047): the application's reason prints on the owner statement; the note does not.
describe('ApplyModal description and internal note', () => {
  it('labels both fields with their audience and posts them separately', async () => {
    let body: Record<string, unknown> | undefined;
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/settings/banks', () => HttpResponse.json(BANKS)),
      http.post('/api/accounting/tenants/:tenantId/deposit-applications', async ({ request }) => {
        body = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json({ entryId: 'a1' });
      }),
    );
    const { onApplied } = renderModal();

    await screen.findByText(/Operating Trust/);
    const description = screen.getByLabelText('Statement description');
    expect(description).toHaveAccessibleDescription('Owner sees this');
    const note = screen.getByLabelText('Internal note');
    expect(note).toHaveAccessibleDescription('Staff only');

    await userEvent.type(screen.getByLabelText('Amount'), '100');
    await userEvent.type(description, 'Move-out settlement');
    await userEvent.type(note, 'Carpet damage per inspection photos');
    await userEvent.click(screen.getByRole('button', { name: 'Apply' }));

    await vi.waitFor(() => expect(onApplied).toHaveBeenCalledWith('a1'));
    expect(body).toMatchObject({
      reason: 'Move-out settlement',
      internalNote: 'Carpet damage per inspection photos',
    });
  });

  it('posts a prepayment application’s description and note', async () => {
    let body: Record<string, unknown> | undefined;
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/settings/banks', () => HttpResponse.json(BANKS)),
      http.post(
        '/api/accounting/tenants/:tenantId/prepayment-applications',
        async ({ request }) => {
          body = (await request.json()) as Record<string, unknown>;
          return HttpResponse.json({ entryId: 'p1' });
        },
      ),
    );
    const { onApplied } = renderModal();

    await screen.findByText(/Operating Trust/);
    await userEvent.selectOptions(screen.getByLabelText('Source'), 'prepayment');
    await userEvent.type(screen.getByLabelText('Amount'), '50');
    await userEvent.type(screen.getByLabelText('Internal note'), 'Tenant asked by email');
    await userEvent.click(screen.getByRole('button', { name: 'Apply' }));

    await vi.waitFor(() => expect(onApplied).toHaveBeenCalledWith('p1'));
    expect(body).toMatchObject({ description: null, internalNote: 'Tenant asked by email' });
  });
});

// #475: a prepayment is applied from the bank that holds it, which only the server knows.
describe('ApplyModal prepayment bank', () => {
  it('leaves the bank to the server instead of naming the operating trust', async () => {
    let body: Record<string, unknown> | undefined;
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/settings/banks', () => HttpResponse.json(BANKS)),
      http.post(
        '/api/accounting/tenants/:tenantId/prepayment-applications',
        async ({ request }) => {
          body = (await request.json()) as Record<string, unknown>;
          return HttpResponse.json({ entryId: 'p1' });
        },
      ),
    );
    const { onApplied } = renderModal();

    await screen.findByText(/Operating Trust/);
    await userEvent.selectOptions(screen.getByLabelText('Source'), 'prepayment');
    expect(screen.queryByText(/Operating Trust/)).toBeNull();
    await userEvent.type(screen.getByLabelText('Amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Apply' }));

    await vi.waitFor(() => expect(onApplied).toHaveBeenCalledWith('p1'));
    expect(body).toMatchObject({ bankAccountId: null });
  });

  it('asks which bank to apply from when the prepaid credit sits in more than one', async () => {
    const bodies: Record<string, unknown>[] = [];
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/settings/banks', () => HttpResponse.json(BANKS)),
      http.get('/api/refund-checks/options', () => HttpResponse.json({ funds: SPLIT_FUNDS })),
      ambiguousUnlessNamed(bodies),
    );
    const { onApplied } = renderModal();

    await askForPrepayment('150');

    expect(await screen.findByRole('alert')).toHaveTextContent(/more than one account/i);
    const picker = await screen.findByRole('group', { name: 'Apply from' });
    // Only the banks holding prepaid credit are offered, with what each holds — and none is preselected:
    // the server refused to guess, so the dialog does not guess either.
    const choices = within(picker).getAllByRole('radio');
    expect(choices.map((choice) => choice.closest('label')?.textContent)).toEqual([
      'Operating TrustHeld $100.00',
      'Deposit TrustHeld $200.00',
    ]);
    expect(choices.every((choice) => !(choice as HTMLInputElement).checked)).toBe(true);

    await userEvent.click(screen.getByRole('button', { name: 'Apply' }));
    expect(await screen.findByText('Choose which account to apply from.')).toBeInTheDocument();
    expect(bodies).toHaveLength(1);
    expect(onApplied).not.toHaveBeenCalled();

    await userEvent.click(within(picker).getByRole('radio', { name: /Deposit Trust/ }));
    await userEvent.click(screen.getByRole('button', { name: 'Apply' }));

    await vi.waitFor(() => expect(onApplied).toHaveBeenCalledWith('p2'));
    expect(bodies.at(-1)).toMatchObject({ bankAccountId: 'dep1', amount: 150 });
  });

  it('blocks the apply and offers a retry when the holding accounts cannot be read', async () => {
    const bodies: Record<string, unknown>[] = [];
    let attempt = 0;
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/settings/banks', () => HttpResponse.json(BANKS)),
      http.get('/api/refund-checks/options', () => {
        attempt += 1;
        return attempt === 1
          ? HttpResponse.json(
              {
                title: 'internal_error',
                status: 500,
                detail: 'Refund options read failed.',
                code: 'internal_error',
                correlationId: 'corr475',
              },
              { status: 500, headers: { 'content-type': 'application/problem+json' } },
            )
          : HttpResponse.json({ funds: SPLIT_FUNDS });
      }),
      ambiguousUnlessNamed(bodies),
    );
    renderModal();

    await askForPrepayment('150');

    // Without the list there is nothing to choose from, so resubmitting would only repeat the refusal.
    expect(await screen.findByText('Something went wrong on our end.')).toBeInTheDocument();
    expect(screen.getByText('Reference: corr475')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Apply' })).toBeDisabled();
    expect(screen.queryByRole('group', { name: 'Apply from' })).toBeNull();
    await userEvent.keyboard('{Enter}');
    expect(bodies).toHaveLength(1);

    await userEvent.click(screen.getByRole('button', { name: /retry/i }));
    expect(await screen.findByRole('group', { name: 'Apply from' })).toBeInTheDocument();
  });
});
