import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { beforeEach, describe, expect, it } from 'vitest';
import { RecordNavProvider } from '@/components/recordNav';
import { localDate } from '@/lib/dates';
import { server } from '@/test/mocks/server';
import { PropertyDetailPage } from './PropertyDetailPage';

const SELLER = { id: 'o1', name: 'Harborview Holdings' };
const BUYER = { id: 'o2', name: 'Meridian Estates' };
const OTHER = { id: 'o3', name: 'Stillwater Properties' };

const PROPERTY = {
  id: 'p1',
  address: '14 Harborview Ln',
  city: 'Wilmington',
  state: 'NC',
  zip: null,
  ownerId: SELLER.id,
  ownerName: SELLER.name,
  mgmtFeeBps: null,
  units: [],
  tenants: [],
};

const today = () => localDate(new Date().toISOString());

function tomorrow(): string {
  const date = new Date();
  date.setDate(date.getDate() + 1);
  return localDate(date.toISOString());
}

interface Posted {
  toOwnerId: string;
  effectiveDate: string;
  sourceRef: string;
}

/**
 * Serves the property and the owner list, and records each transfer the page posts. A successful
 * transfer changes the owner the next property read returns, as the server does.
 */
function serve(
  options: {
    owners?: (typeof SELLER)[];
    refuse?: { status: number; body: Record<string, unknown> };
    depositEntryId?: string | null;
  } = {},
) {
  const posted: Posted[] = [];
  let property = { ...PROPERTY };
  const owners = options.owners ?? [SELLER, BUYER, OTHER];
  server.use(
    http.get('/api/directory/properties/:id', () => HttpResponse.json(property)),
    http.get('/api/directory/owners', () =>
      HttpResponse.json({ items: owners, total: owners.length }),
    ),
    http.post('/api/directory/properties/:id/ownership-transfers', async ({ request }) => {
      const body = (await request.json()) as Posted;
      posted.push(body);
      if (options.refuse) return HttpResponse.json(options.refuse.body, options.refuse);
      const buyer = owners.find((o) => o.id === body.toOwnerId)!;
      property = { ...property, ownerId: buyer.id, ownerName: buyer.name };
      return HttpResponse.json({
        propertyId: PROPERTY.id,
        fromOwnerId: SELLER.id,
        toOwnerId: buyer.id,
        effectiveDate: body.effectiveDate,
        depositEntryId: options.depositEntryId === undefined ? 'e1' : options.depositEntryId,
      });
    }),
  );
  return posted;
}

function renderProperty() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      { path: '/properties', element: <div>properties index</div> },
      { path: '/properties/:id', element: <PropertyDetailPage /> },
    ],
    { initialEntries: ['/properties/p1'] },
  );
  render(
    <QueryClientProvider client={queryClient}>
      <RecordNavProvider>
        <RouterProvider router={router} />
      </RecordNavProvider>
    </QueryClientProvider>,
  );
}

async function openTransfer() {
  await userEvent.click(await screen.findByRole('button', { name: 'Transfer ownership' }));
  return screen.getByRole('dialog', { name: 'Transfer 14 Harborview Ln' });
}

const confirmButton = (dialog: HTMLElement) =>
  within(dialog).getByRole('button', { name: 'Transfer ownership' });

beforeEach(() => {
  document.body.innerHTML = '';
});

describe('Transfer ownership (#555, ADR-036)', () => {
  it('offers only the other owners, dated today unless the operator backdates it', async () => {
    serve();
    renderProperty();
    const dialog = await openTransfer();

    const picker = await within(dialog).findByLabelText('New owner');
    const names = within(picker)
      .getAllByRole('option')
      .map((option) => option.textContent);
    expect(names).toEqual(['Select the new owner…', BUYER.name, OTHER.name]);

    const date = within(dialog).getByLabelText('Effective date');
    expect(date).toHaveValue(today());
    expect(date).toHaveAttribute('max', today());
  });

  it('says what moves to the new owner and what the transfer does not settle', async () => {
    serve();
    renderProperty();
    const dialog = await openTransfer();

    expect(dialog).toHaveTextContent(
      /Security deposits held for this property's tenants move to the new owner/,
    );
    expect(dialog).toHaveTextContent(/does not settle the current owner's equity/);
  });

  it('records the transfer, and the page names the new owner', async () => {
    const posted = serve();
    renderProperty();
    const dialog = await openTransfer();

    await userEvent.selectOptions(await within(dialog).findByLabelText('New owner'), BUYER.id);
    await userEvent.click(confirmButton(dialog));

    expect(await screen.findByText(/Owner: Meridian Estates/)).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(screen.getByRole('status')).toHaveTextContent(
      `Transferred to Meridian Estates, effective ${today()}. The held security deposits moved with it.`,
    );
    expect(posted).toHaveLength(1);
    expect(posted[0]).toMatchObject({ toOwnerId: BUYER.id, effectiveDate: today() });
    expect(posted[0]!.sourceRef).not.toBe('');
  });

  it('says so when the property held no deposits to move', async () => {
    serve({ depositEntryId: null });
    renderProperty();
    const dialog = await openTransfer();

    await userEvent.selectOptions(await within(dialog).findByLabelText('New owner'), BUYER.id);
    await userEvent.click(confirmButton(dialog));

    expect(await screen.findByRole('status')).toHaveTextContent(
      'No security deposits were held for this property, so none moved.',
    );
  });

  it('confirms from the keyboard with Enter in the date field', async () => {
    const posted = serve();
    renderProperty();
    const dialog = await openTransfer();

    await userEvent.selectOptions(await within(dialog).findByLabelText('New owner'), BUYER.id);
    await userEvent.type(within(dialog).getByLabelText('Effective date'), '{Enter}');

    expect(await screen.findByText(/Owner: Meridian Estates/)).toBeInTheDocument();
    expect(posted).toHaveLength(1);
  });

  it('asks for the new owner before posting anything', async () => {
    const posted = serve();
    renderProperty();
    const dialog = await openTransfer();
    await within(dialog).findByLabelText('New owner');

    await userEvent.click(confirmButton(dialog));

    expect(within(dialog).getByRole('alert')).toHaveTextContent('Choose the new owner.');
    expect(posted).toHaveLength(0);
  });

  it('refuses a future date before posting anything', async () => {
    const posted = serve();
    renderProperty();
    const dialog = await openTransfer();

    await userEvent.selectOptions(await within(dialog).findByLabelText('New owner'), BUYER.id);
    const date = within(dialog).getByLabelText('Effective date');
    await userEvent.clear(date);
    await userEvent.type(date, tomorrow());
    await userEvent.click(confirmButton(dialog));

    expect(within(dialog).getByRole('alert')).toHaveTextContent(
      'The effective date can’t be in the future. Record the transfer on or after the day it takes effect.',
    );
    expect(posted).toHaveLength(0);
  });

  it.each([
    [
      'a backdated transfer with later deposit activity',
      'effectiveDate',
      'Later seller-attributed deposit activity must be reversed before recording this backdated ownership transfer.',
    ],
    [
      'a transfer no later than the one before it',
      'effectiveDate',
      'A property ownership transfer must be effective after its preceding transfer.',
    ],
    [
      'a transfer to the current owner',
      'toOwnerId',
      'The succeeding owner must differ from the current owner.',
    ],
    [
      'a future date by the server’s clock',
      'EffectiveDate',
      'A property ownership transfer cannot be scheduled for a future date.',
    ],
  ])('shows the refusal of %s inline and keeps the dialog open', async (_, field, message) => {
    serve({
      refuse: {
        status: 400,
        body: { title: 'One or more validation errors occurred.', errors: { [field]: [message] } },
      },
    });
    renderProperty();
    const dialog = await openTransfer();

    await userEvent.selectOptions(await within(dialog).findByLabelText('New owner'), BUYER.id);
    await userEvent.click(confirmButton(dialog));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(message);
    expect(screen.getByRole('dialog')).toBe(dialog);
    expect(screen.getByText(/Owner: Harborview Holdings/)).toBeInTheDocument();
  });

  // The server's lock copy says to post into the open month, which a transfer cannot; the dialog
  // names the date instead, and only an administrator is told to unlock (#554).
  it.each([
    [
      'PMStaff',
      'account_period_locked',
      'A bank account holding these deposits is reconciled and locked for that month. Choose a later date, or ask an administrator to unlock it.',
    ],
    [
      'PMAdmin',
      'account_period_locked',
      'A bank account holding these deposits is reconciled and locked for that month. Choose a later date, or unlock the month from its reconciliation history on Banking.',
    ],
    [
      'PMStaff',
      'period_closed',
      'The accounting period for that date is closed. Choose a date in an open period.',
    ],
  ])('words a %s refusal for %s around the date', async (role, code, message) => {
    serve({ refuse: { status: 409, body: { code, detail: 'Post into the open month.' } } });
    server.use(http.get('/api/auth/me', () => HttpResponse.json({ email: 'a@b.test', role })));
    renderProperty();
    const dialog = await openTransfer();

    await userEvent.selectOptions(await within(dialog).findByLabelText('New owner'), BUYER.id);
    await userEvent.click(confirmButton(dialog));

    const alert = await within(dialog).findByRole('alert');
    expect(alert).toHaveTextContent(message);
    expect(alert).not.toHaveTextContent('open month');
  });

  it('explains a failed owner list instead of an empty picker, and cannot confirm', async () => {
    const posted = serve();
    server.use(http.get('/api/directory/owners', () => new HttpResponse(null, { status: 503 })));
    renderProperty();
    const dialog = await openTransfer();

    expect(await within(dialog).findByRole('alert')).toBeInTheDocument();
    expect(within(dialog).queryByLabelText('New owner')).toBeNull();
    expect(confirmButton(dialog)).toBeDisabled();
    expect(posted).toHaveLength(0);
  });

  it('says to add the buyer first when no other owner exists', async () => {
    serve({ owners: [SELLER] });
    renderProperty();
    const dialog = await openTransfer();

    expect(
      await within(dialog).findByText(
        'No other owner to transfer to — add the buyer as an owner first.',
      ),
    ).toBeInTheDocument();
    expect(confirmButton(dialog)).toBeDisabled();
  });

  it('shows the owner list loading', async () => {
    serve();
    server.use(http.get('/api/directory/owners', () => new Promise(() => {})));
    renderProperty();
    const dialog = await openTransfer();

    expect(within(dialog).getByRole('status', { name: 'Loading owners' })).toBeInTheDocument();
    expect(confirmButton(dialog)).toBeDisabled();
  });
});
