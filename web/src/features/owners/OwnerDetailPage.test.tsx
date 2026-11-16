import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { beforeEach, describe, expect, it } from 'vitest';
import { RecordNavProvider } from '@/components/recordNav';
import { server } from '@/test/mocks/server';
import { OwnerDetailPage } from './OwnerDetailPage';

const OWNER = {
  id: 'o1',
  name: 'Hargrove Family Trust',
  defaultMgmtFeeBps: 800,
  contact: { email: 'trust@example.test', phone: '828-555-0142' },
  operating: 4200,
  deposits: 1450,
  total: 5650,
  reserveAmount: 500,
  properties: [{ id: 'p1', address: '412 Oakmont Ave', city: 'Raleigh', units: 2, occupied: 2 }],
};

const REFERENCE = 'a1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4';

function renderOwner() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      { path: '/owners', element: <div>owners index</div> },
      { path: '/owners/:id', element: <OwnerDetailPage /> },
    ],
    { initialEntries: ['/owners/o1'] },
  );
  render(
    <QueryClientProvider client={queryClient}>
      <RecordNavProvider>
        <RouterProvider router={router} />
      </RecordNavProvider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  document.body.innerHTML = '';
});

// DetailPage is the shared scaffold behind every /owners/:id and /properties/:id route, so its
// error branch is exercised here through a real one rather than against a fabricated query.
describe('DetailPage read errors, via OwnerDetailPage', () => {
  it('surfaces the server message and support reference instead of "Record not found"', async () => {
    server.use(
      http.get('/api/directory/owners/:id', () =>
        HttpResponse.json(
          { detail: 'The owner service is unavailable.', correlationId: REFERENCE },
          { status: 503 },
        ),
      ),
    );
    renderOwner();

    expect(await screen.findByText('Couldn’t load this record')).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent('The owner service is unavailable.');
    expect(screen.getByText(`Reference: ${REFERENCE}`)).toBeInTheDocument();

    // A failed read is not evidence that the record is gone, and telling the operator it is sends
    // them to look for a deletion that never happened.
    expect(screen.queryByText('Record not found')).toBeNull();
  });

  it('still says "Record not found" for a 404, which retrying cannot fix', async () => {
    server.use(
      http.get('/api/directory/owners/:id', () => new HttpResponse(null, { status: 404 })),
    );
    renderOwner();

    expect(await screen.findByText('Record not found')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
  });

  it('recovers in place when the retry succeeds', async () => {
    let attempt = 0;
    server.use(
      http.get('/api/directory/owners/:id', () => {
        attempt += 1;
        return attempt === 1 ? new HttpResponse(null, { status: 500 }) : HttpResponse.json(OWNER);
      }),
    );
    renderOwner();

    await userEvent.click(await screen.findByRole('button', { name: 'Retry' }));

    expect(await screen.findByText('Hargrove Family Trust')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});

// #782: a property manager corrects an owner from its page. The update replaces the whole owner, so
// the dialog sends back every field, prefilled from the read.
describe('Edit an owner', () => {
  const EDITABLE = {
    ...OWNER,
    initials: 'HFT',
    defaultMgmtFeeBps: 833,
    reserveAmount: 250,
  };

  interface Refusal {
    status: number;
    body: object;
  }

  /** Serves the owner as last saved, and records every update sent. */
  function serveOwner(refuse?: Refusal) {
    let current: Record<string, unknown> = { ...EDITABLE };
    const sent: Record<string, unknown>[] = [];
    server.use(
      http.get('/api/directory/owners/o1', () => HttpResponse.json(current)),
      http.put('/api/directory/owners/o1', async ({ request }) => {
        const body = (await request.json()) as Record<string, unknown>;
        sent.push(body);
        if (refuse) return HttpResponse.json(refuse.body, { status: refuse.status });
        current = {
          ...current,
          name: body.name,
          initials: body.initials,
          contact: { email: body.contactEmail, phone: body.contactPhone },
          defaultMgmtFeeBps: body.defaultMgmtFeeBps,
          reserveAmount: body.reserveAmount,
        };
        return new HttpResponse(null, { status: 204 });
      }),
    );
    return sent;
  }

  /** Saves from the focused button: the keyboard path of a dialog that sets money policy (#711). */
  async function save(dialog: HTMLElement) {
    within(dialog).getByRole('button', { name: 'Save owner' }).focus();
    await userEvent.keyboard('{Enter}');
  }

  async function openEdit() {
    const edit = await screen.findByRole('button', { name: 'Edit' });
    edit.focus();
    await userEvent.keyboard('{Enter}');
    return screen.getByRole('dialog', { name: 'Edit Hargrove Family Trust' });
  }

  it('prefills every field, initials included, and an untouched save sends the owner back as it was', async () => {
    const sent = serveOwner();
    renderOwner();

    const dialog = await openEdit();
    expect(within(dialog).getByLabelText('Name')).toHaveValue('Hargrove Family Trust');
    expect(within(dialog).getByLabelText('Initials')).toHaveValue('HFT');
    expect(within(dialog).getByLabelText('Email')).toHaveValue('trust@example.test');
    expect(within(dialog).getByLabelText('Phone')).toHaveValue('828-555-0142');
    // Basis points shown as a percent: 833 is 8.33%.
    expect(within(dialog).getByLabelText('Mgmt fee %')).toHaveValue(8.33);
    expect(within(dialog).getByLabelText('Reserve floor')).toHaveValue(250);

    await save(dialog);

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(sent).toEqual([
      {
        id: 'o1',
        name: 'Hargrove Family Trust',
        initials: 'HFT',
        contactEmail: 'trust@example.test',
        contactPhone: '828-555-0142',
        defaultMgmtFeeBps: 833,
        reserveAmount: 250,
      },
    ]);
  });

  it('saves an edit by keyboard, and the page shows the new name and fee rate without a reload', async () => {
    const sent = serveOwner();
    renderOwner();

    const dialog = await openEdit();
    const name = within(dialog).getByLabelText('Name');
    await userEvent.clear(name);
    await userEvent.type(name, 'Hargrove Family Trust II');
    const fee = within(dialog).getByLabelText('Mgmt fee %');
    await userEvent.clear(fee);
    await userEvent.type(fee, '8.25');
    const reserve = within(dialog).getByLabelText('Reserve floor');
    await userEvent.clear(reserve);
    await userEvent.type(reserve, '750.50');
    await save(dialog);

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(sent).toEqual([
      {
        id: 'o1',
        name: 'Hargrove Family Trust II',
        initials: 'HFT',
        contactEmail: 'trust@example.test',
        contactPhone: '828-555-0142',
        defaultMgmtFeeBps: 825,
        reserveAmount: 750.5,
      },
    ]);
    expect(
      await screen.findByRole('heading', { name: 'Hargrove Family Trust II', level: 2 }),
    ).toBeVisible();
    expect(screen.getByText('Default management fee · 8.25%')).toBeVisible();
  });

  it('round-trips the fee rate exactly through basis points, and a blank rate means no default fee', async () => {
    const sent = serveOwner();
    renderOwner();

    let dialog = await openEdit();
    const fee = () => within(dialog).getByLabelText('Mgmt fee %');
    await userEvent.clear(fee());
    await userEvent.type(fee(), '0.01');
    await save(dialog);
    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]).toMatchObject({ defaultMgmtFeeBps: 1 });

    dialog = await openEdit();
    expect(fee()).toHaveValue(0.01);
    await userEvent.clear(fee());
    await userEvent.type(fee(), '100');
    await save(dialog);
    await waitFor(() => expect(sent).toHaveLength(2));
    expect(sent[1]).toMatchObject({ defaultMgmtFeeBps: 10000 });

    dialog = await openEdit();
    expect(fee()).toHaveValue(100);
    await userEvent.clear(fee());
    await save(dialog);
    await waitFor(() => expect(sent).toHaveLength(3));
    expect(sent[2]).toMatchObject({ defaultMgmtFeeBps: null });
    expect(await screen.findByText('No default management fee')).toBeVisible();
  });

  it('clears an emptied email, phone or initials, and bounds every field the server bounds', async () => {
    const sent = serveOwner();
    renderOwner();

    const dialog = await openEdit();
    expect(within(dialog).getByLabelText('Name')).toBeRequired();
    expect(within(dialog).getByLabelText('Name')).toHaveAttribute('maxLength', '200');
    expect(within(dialog).getByLabelText('Initials')).toHaveAttribute('maxLength', '8');
    expect(within(dialog).getByLabelText('Email')).toHaveAttribute('type', 'email');
    const fee = within(dialog).getByLabelText('Mgmt fee %');
    expect(fee).toHaveAttribute('min', '0');
    expect(fee).toHaveAttribute('max', '100');
    expect(fee).toHaveAttribute('step', '0.01');
    const reserve = within(dialog).getByLabelText('Reserve floor');
    expect(reserve).toBeRequired();
    expect(reserve).toHaveAttribute('min', '0');
    expect(reserve).toHaveAttribute('step', '0.01');

    for (const label of ['Initials', 'Email', 'Phone']) {
      await userEvent.clear(within(dialog).getByLabelText(label));
    }
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save owner' }));
    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]).toMatchObject({ initials: null, contactEmail: null, contactPhone: null });
  });

  it('never saves from Enter in a field, and reports a field out of bounds before anything is sent', async () => {
    const sent = serveOwner();
    renderOwner();

    const dialog = await openEdit();
    const fee = within(dialog).getByLabelText('Mgmt fee %');
    await userEvent.clear(fee);
    // A stray Enter in a field sets no money policy (#711).
    await userEvent.type(fee, '9{Enter}');
    expect(screen.getByRole('dialog')).toBeVisible();
    expect(sent).toEqual([]);

    // Over 100% is out of bounds: the button reports it on the field, which takes focus.
    await userEvent.clear(fee);
    await userEvent.type(fee, '150');
    await save(dialog);
    expect(fee).toBeInvalid();
    expect(fee).toHaveFocus();
    expect(sent).toEqual([]);
  });

  it('says a fee-rate or reserve change applies from the next run', async () => {
    serveOwner();
    renderOwner();

    const dialog = await openEdit();
    expect(within(dialog).getByText(/applies from the next run/i)).toBeVisible();
  });

  it('shows the server’s refusal with its support reference and keeps the dialog open', async () => {
    const detail = 'The management fee must be between 0 and 10000 basis points.';
    const sent = serveOwner({
      status: 400,
      body: { code: 'validation_failed', detail, correlationId: 'corr-782' },
    });
    renderOwner();

    const dialog = await openEdit();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save owner' }));

    expect(await within(dialog).findByText(new RegExp(detail))).toBeVisible();
    expect(within(dialog).getByText(/Reference: corr-782/)).toBeVisible();
    expect(sent).toHaveLength(1);
    expect(screen.getByRole('dialog')).toBeVisible();
    expect(screen.getByRole('heading', { name: 'Hargrove Family Trust', level: 2 })).toBeVisible();
  });
});
