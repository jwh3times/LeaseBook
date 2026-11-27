import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { RecordNavProvider } from '@/components/recordNav';
import { localDate } from '@/lib/dates';
import { ORG_SETTINGS } from '@/test/mocks/handlers';
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

  // Enter in a field transfers nothing (#711); the keyboard path is Tab past Cancel to the button.
  it('confirms from the keyboard on the transfer button', async () => {
    const posted = serve();
    renderProperty();
    const dialog = await openTransfer();

    await userEvent.selectOptions(await within(dialog).findByLabelText('New owner'), BUYER.id);
    await userEvent.click(within(dialog).getByLabelText('Effective date'));
    await userEvent.tab();
    await userEvent.tab();
    expect(confirmButton(dialog)).toHaveFocus();
    await userEvent.keyboard('{Enter}');

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

  // #642: the server refuses a date after the organization's today the way it refuses money dated so.
  it('shows the server’s refusal of a date after the organization’s today inline', async () => {
    const message =
      'The date 2026-11-01 is after today, 2026-10-31, in the organization’s time zone.';
    serve({
      refuse: {
        status: 422,
        body: { code: 'posting_date_in_future', detail: message, correlationId: 'corr-642' },
      },
    });
    renderProperty();
    const dialog = await openTransfer();

    await userEvent.selectOptions(await within(dialog).findByLabelText('New owner'), BUYER.id);
    await userEvent.click(confirmButton(dialog));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(message);
    expect(screen.getByRole('dialog')).toBe(dialog);
  });

  it('blocks the transfer and says why while the organization’s settings cannot be read', async () => {
    const posted = serve();
    server.use(
      http.get('/api/settings/org', () =>
        HttpResponse.json(
          {
            code: 'boom',
            detail: 'Settings are down for maintenance.',
            correlationId: 'corr-settings',
          },
          { status: 503, headers: { 'content-type': 'application/problem+json' } },
        ),
      ),
    );
    renderProperty();
    const dialog = await openTransfer();

    expect(
      await within(dialog).findByText(/Settings are down for maintenance\./),
    ).toBeInTheDocument();
    expect(within(dialog).getByText(/Reference: corr-settings/)).toBeInTheDocument();
    expect(confirmButton(dialog)).toBeDisabled();
    expect(posted).toHaveLength(0);
  });

  // At 06:30 UTC on 1 Nov 2026 it is 23:30 on 31 Oct in Los Angeles, where the books are kept, and
  // already 1 Nov in New York, where the viewer is.
  it('defaults to, and allows no later than, the organization’s today in its own zone', async () => {
    vi.setSystemTime(new Date('2026-11-01T06:30:00Z'));
    const posted = serve();
    server.use(
      http.get('/api/settings/org', () =>
        HttpResponse.json({ ...ORG_SETTINGS, timeZone: 'America/Los_Angeles' }),
      ),
    );
    renderProperty();
    const dialog = await openTransfer();

    const date = within(dialog).getByLabelText('Effective date');
    await vi.waitFor(() => expect(date).toHaveValue('2026-10-31'));
    expect(date).toHaveAttribute('max', '2026-10-31');

    await userEvent.selectOptions(await within(dialog).findByLabelText('New owner'), BUYER.id);
    await userEvent.clear(date);
    await userEvent.type(date, '2026-11-01');
    await userEvent.click(confirmButton(dialog));
    // The field's own max refuses it before anything is sent (#711).
    expect(date).toHaveFocus();
    expect(date).toBeInvalid();
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

    // The field's own max refuses it, and focus goes to it, before anything is sent (#711).
    expect(date).toHaveFocus();
    expect(date).toBeInvalid();
    expect(posted).toHaveLength(0);

    await userEvent.clear(date);
    await userEvent.type(date, today());
    await userEvent.click(confirmButton(dialog));
    await vi.waitFor(() => expect(posted).toHaveLength(1));
  });

  it('never transfers from Enter in a field', async () => {
    const posted = serve();
    renderProperty();
    const dialog = await openTransfer();

    await userEvent.selectOptions(await within(dialog).findByLabelText('New owner'), BUYER.id);
    await userEvent.keyboard('{Enter}');
    await userEvent.click(within(dialog).getByLabelText('Effective date'));
    await userEvent.keyboard('{Enter}');
    expect(posted).toHaveLength(0);

    // The same choice transfers from the button, so the Enters above had something to send.
    await userEvent.click(confirmButton(dialog));
    await vi.waitFor(() => expect(posted).toHaveLength(1));
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

interface UnitSent {
  id?: string;
  propertyId?: string;
  label: string;
  rent: number | string;
  availability: string;
}

const UNIT_2B = {
  id: 'u1',
  propertyId: PROPERTY.id,
  label: '#2B',
  rent: 1450,
  occupancy: 'vacant',
  availability: 'available',
};

/**
 * Serves the property with `units`, and records each unit the page adds or saves. A successful write
 * changes what the next property read returns, as the server does.
 */
function serveUnits(
  options: {
    units?: (typeof UNIT_2B)[];
    refuse?: { status: number; body: Record<string, unknown> };
  } = {},
) {
  const sent: UnitSent[] = [];
  let units = [...(options.units ?? [])];
  const refusal = () =>
    HttpResponse.json(options.refuse!.body, {
      status: options.refuse!.status,
      headers: { 'content-type': 'application/problem+json' },
    });
  server.use(
    http.get('/api/directory/properties/:id', () => HttpResponse.json({ ...PROPERTY, units })),
    http.post('/api/directory/units', async ({ request }) => {
      const body = (await request.json()) as UnitSent;
      sent.push(body);
      if (options.refuse) return refusal();
      units = [
        ...units,
        { ...UNIT_2B, ...body, id: 'uNew', occupancy: 'vacant' } as typeof UNIT_2B,
      ];
      return HttpResponse.json({ id: 'uNew' });
    }),
    http.put('/api/directory/units/:id', async ({ request, params }) => {
      const body = (await request.json()) as UnitSent;
      sent.push({ ...body, id: String(params.id) });
      if (options.refuse) return refusal();
      units = units.map((u) => (u.id === params.id ? ({ ...u, ...body } as typeof UNIT_2B) : u));
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return sent;
}

const unitsCard = async () =>
  (await screen.findByRole('heading', { name: 'Units' })).closest('.pf-card') as HTMLElement;

describe('Add and edit a unit (#780)', () => {
  it('heads the unit figure "Asking rent", the rent the unit is offered at', async () => {
    serveUnits({ units: [UNIT_2B] });
    renderProperty();

    const card = await unitsCard();
    expect(await within(card).findByRole('columnheader', { name: 'Asking rent' })).toBeVisible();
    expect(within(card).queryByRole('columnheader', { name: 'Rent' })).toBeNull();
  });

  it('adds a unit by Enter, available unless chosen otherwise, and the card lists it', async () => {
    const sent = serveUnits();
    renderProperty();

    await userEvent.click(await screen.findByRole('button', { name: 'Add unit' }));
    const dialog = screen.getByRole('dialog', { name: 'Add a unit to 14 Harborview Ln' });
    expect(within(dialog).getByLabelText('Availability')).toHaveValue('available');
    await userEvent.type(within(dialog).getByLabelText('Label'), '#3C');
    await userEvent.type(within(dialog).getByLabelText('Asking rent'), '1525.50{Enter}');

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(sent).toEqual([
      { propertyId: PROPERTY.id, label: '#3C', rent: 1525.5, availability: 'available' },
    ]);
    const card = await unitsCard();
    expect(await within(card).findByRole('cell', { name: '#3C' })).toBeVisible();
  });

  it('refuses a unit with no label before anything is sent, and bounds the asking rent', async () => {
    const sent = serveUnits();
    renderProperty();

    await userEvent.click(await screen.findByRole('button', { name: 'Add unit' }));
    const dialog = screen.getByRole('dialog', { name: 'Add a unit to 14 Harborview Ln' });
    const label = within(dialog).getByLabelText('Label');
    const rent = within(dialog).getByLabelText('Asking rent');
    expect(label).toBeRequired();
    expect(label).toHaveAttribute('maxLength', '60');
    expect(rent).toBeRequired();
    expect(rent).toHaveAttribute('min', '0');
    expect(rent).toHaveAttribute('step', '0.01');

    await userEvent.type(rent, '1200{Enter}');
    expect(sent).toHaveLength(0);
    expect(screen.getByRole('dialog')).toBe(dialog);
  });

  it('edits a unit from its row, opening on its current values, and the card shows the change', async () => {
    const sent = serveUnits({ units: [UNIT_2B] });
    renderProperty();

    const card = await unitsCard();
    await userEvent.click(await within(card).findByRole('button', { name: 'Edit unit #2B' }));
    const dialog = screen.getByRole('dialog', { name: 'Edit unit #2B' });
    expect(within(dialog).getByLabelText('Label')).toHaveValue('#2B');
    expect(within(dialog).getByLabelText('Asking rent')).toHaveValue(1450);
    expect(within(dialog).getByLabelText('Availability')).toHaveValue('available');

    const rent = within(dialog).getByLabelText('Asking rent');
    await userEvent.clear(rent);
    await userEvent.type(rent, '1495');
    await userEvent.selectOptions(within(dialog).getByLabelText('Availability'), 'unavailable');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save unit' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(sent).toEqual([{ id: 'u1', label: '#2B', rent: 1495, availability: 'unavailable' }]);
    expect(await within(card).findByText('Unavailable')).toBeVisible();
  });

  it('shows the server’s refusal with its support reference and keeps the dialog open', async () => {
    const detail = 'Label must be 60 characters or fewer.';
    serveUnits({
      units: [UNIT_2B],
      refuse: {
        status: 400,
        body: { code: 'validation_failed', detail, correlationId: 'corr-780' },
      },
    });
    renderProperty();

    const card = await unitsCard();
    await userEvent.click(await within(card).findByRole('button', { name: 'Edit unit #2B' }));
    const dialog = screen.getByRole('dialog', { name: 'Edit unit #2B' });
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save unit' }));

    expect(await within(dialog).findByText(new RegExp(detail))).toBeVisible();
    expect(within(dialog).getByText(/Reference: corr-780/)).toBeVisible();
    expect(screen.getByRole('dialog')).toBe(dialog);
  });
});

interface PropertySent {
  id: string;
  address: string;
  city: string | null;
  state: string | null;
  zip: string | null;
  mgmtFeeBps: number | string | null;
}

/**
 * Serves the property, carrying a management-fee override the page does not show, and records each
 * save. A successful save changes what the next property read returns, as the server does.
 */
function serveEdit(
  options: {
    mgmtFeeBps?: number | null;
    refuse?: { status: number; body: Record<string, unknown> };
    /** Holds the save's answer until it resolves, so a test can see the dialog while it saves. */
    hold?: Promise<void>;
  } = {},
) {
  const sent: PropertySent[] = [];
  let property: Omit<typeof PROPERTY, keyof PropertySent> & PropertySent = {
    ...PROPERTY,
    mgmtFeeBps: options.mgmtFeeBps === undefined ? 650 : options.mgmtFeeBps,
  };
  server.use(
    http.get('/api/directory/properties/:id', () => HttpResponse.json(property)),
    http.put('/api/directory/properties/:id', async ({ request, params }) => {
      const body = (await request.json()) as PropertySent;
      sent.push({ ...body, id: String(params.id) });
      await options.hold;
      if (options.refuse)
        return HttpResponse.json(options.refuse.body, {
          status: options.refuse.status,
          headers: { 'content-type': 'application/problem+json' },
        });
      property = { ...property, ...body };
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return sent;
}

async function openEdit() {
  await userEvent.click(await screen.findByRole('button', { name: 'Edit' }));
  return screen.getByRole('dialog', { name: 'Edit 14 Harborview Ln' });
}

describe('Edit a property (#783)', () => {
  it('opens on the stored address, and the header shows the saved change', async () => {
    const sent = serveEdit();
    renderProperty();

    const dialog = await openEdit();
    expect(within(dialog).getByLabelText('Address')).toHaveValue('14 Harborview Ln');
    expect(within(dialog).getByLabelText('City')).toHaveValue('Wilmington');
    expect(within(dialog).getByLabelText('State')).toHaveValue('NC');
    expect(within(dialog).getByLabelText('Zip')).toHaveValue('');
    // The override is not offered: nothing applies it today.
    expect(within(dialog).queryByLabelText(/fee/i)).toBeNull();

    const address = within(dialog).getByLabelText('Address');
    await userEvent.clear(address);
    // Fields are saved trimmed: the required address as well as an optional field.
    await userEvent.type(address, '  16 Harborview Ln  ');
    await userEvent.type(within(dialog).getByLabelText('Zip'), '  28401  {Enter}');

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(sent).toEqual([
      {
        id: 'p1',
        address: '16 Harborview Ln',
        city: 'Wilmington',
        state: 'NC',
        zip: '28401',
        mgmtFeeBps: 650,
      },
    ]);
    expect(await screen.findByRole('heading', { name: '16 Harborview Ln' })).toBeVisible();
    expect(screen.getByText(/Wilmington, NC, 28401/)).toBeVisible();
  });

  it('sends the management-fee override back exactly as stored, and clears an emptied field', async () => {
    const sent = serveEdit();
    renderProperty();

    const dialog = await openEdit();
    await userEvent.clear(within(dialog).getByLabelText('City'));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save property' }));

    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]).toMatchObject({ city: null, mgmtFeeBps: 650 });
  });

  it('keeps a property with no override at none, never a zero override', async () => {
    const sent = serveEdit({ mgmtFeeBps: null });
    renderProperty();

    const dialog = await openEdit();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save property' }));

    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]!.mgmtFeeBps).toBeNull();
  });

  it('says it is saving and takes no second save until the first is answered', async () => {
    let release = () => {};
    const sent = serveEdit({ hold: new Promise<void>((resolve) => (release = resolve)) });
    renderProperty();

    const dialog = await openEdit();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save property' }));
    const saving = await within(dialog).findByRole('button', { name: 'Saving…' });
    expect(saving).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText('Zip'), '{Enter}');
    expect(sent).toHaveLength(1);

    release();
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(sent).toHaveLength(1);
  });

  it('requires an address and bounds every field before anything is sent', async () => {
    const sent = serveEdit();
    renderProperty();

    const dialog = await openEdit();
    const address = within(dialog).getByLabelText('Address');
    expect(address).toBeRequired();
    expect(address).toHaveAttribute('maxLength', '200');
    expect(within(dialog).getByLabelText('City')).toHaveAttribute('maxLength', '100');
    expect(within(dialog).getByLabelText('State')).toHaveAttribute('maxLength', '50');
    expect(within(dialog).getByLabelText('Zip')).toHaveAttribute('maxLength', '20');

    await userEvent.clear(address);
    await userEvent.type(within(dialog).getByLabelText('City'), '{Enter}');
    expect((address as HTMLInputElement).validity.valueMissing).toBe(true);
    expect(screen.getByRole('dialog')).toBe(dialog);

    // Once the address is filled in, the next save is the only one the server ever receives.
    await userEvent.type(address, '16 Harborview Ln{Enter}');
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(sent.map((body) => body.address)).toEqual(['16 Harborview Ln']);
  });

  it('shows the server’s refusal with its support reference and keeps the dialog open', async () => {
    const detail = 'Address must be 200 characters or fewer.';
    serveEdit({
      refuse: {
        status: 400,
        body: { code: 'validation_failed', detail, correlationId: 'corr-783p' },
      },
    });
    renderProperty();

    const dialog = await openEdit();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save property' }));

    expect(await within(dialog).findByText(new RegExp(detail))).toBeVisible();
    expect(within(dialog).getByText(/Reference: corr-783p/)).toBeVisible();
    expect(screen.getByRole('dialog')).toBe(dialog);
  });

  it('closes on Cancel without saving', async () => {
    const sent = serveEdit();
    renderProperty();

    const dialog = await openEdit();
    await userEvent.type(within(dialog).getByLabelText('Address'), ' Rear');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));

    expect(screen.queryByRole('dialog')).toBeNull();
    expect(sent).toHaveLength(0);
    expect(screen.getByRole('heading', { name: '14 Harborview Ln' })).toBeVisible();
  });
});
