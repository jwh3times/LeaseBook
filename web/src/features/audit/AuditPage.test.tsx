import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { AuditPage } from './AuditPage';

const ADMIN_SESSION = {
  userId: 'user-1',
  name: 'Renée Calloway',
  email: 'renee@example.com',
  role: 'PMAdmin',
  orgId: 'org-1',
  orgName: 'Blue Ridge PM',
  mfaEnabled: true,
  mfaEnrollmentRequired: false,
};

const FILTER_OPTIONS = {
  entityTypes: ['journal_entries', 'tenants'],
  actions: ['insert', 'update'],
  actors: [{ id: 'user-1', name: 'Renée Calloway', email: 'renee@example.com' }],
};

const EVENTS = {
  rows: [
    {
      id: 'event-1',
      occurredAt: '2026-09-11T14:02:00Z',
      entityType: 'tenants',
      entityId: 'tenant-1',
      action: 'update',
      actorName: 'Renée Calloway',
      actorEmail: 'renee@example.com',
    },
  ],
  total: 1,
  page: 1,
  pageSize: 50,
};

/**
 * What a real 500 produces: `UnhandledExceptionHandler` stamps `internal_error`, and the SPA answers
 * it with its own copy plus the support reference `unwrap` carried through (ADR-025).
 * <p>
 * Asserting the heading alone would survive the regression these tests exist to catch, so every error
 * case asserts the message and the reference — and that the message is the **read** wording. The
 * write wording ("Nothing was saved") is a false claim about an operation that never saved, and
 * dropping `kind="read"` is the one-character way to produce it.
 */
const CORRELATION_ID = 'abcdef01234567890abcdef012345678';
const READ_FAILURE_COPY = 'Something went wrong on our end.';

function problemResponse() {
  return HttpResponse.json(
    {
      title: 'internal_error',
      status: 500,
      detail: 'Audit read failed.',
      code: 'internal_error',
      correlationId: CORRELATION_ID,
    },
    { status: 500, headers: { 'content-type': 'application/problem+json' } },
  );
}

function expectReadFailure() {
  expect(screen.getByText(READ_FAILURE_COPY)).toBeInTheDocument();
  expect(screen.getByText(`Reference: ${CORRELATION_ID}`)).toBeInTheDocument();
  expect(screen.queryByText(/Nothing was saved/)).not.toBeInTheDocument();
}

/** The rows on screen, not the option lists that share their vocabulary. */
function eventsTable() {
  return screen.getByRole('table');
}

function adminSession() {
  return http.get('/api/auth/me', () => HttpResponse.json(ADMIN_SESSION));
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <AuditPage />
    </QueryClientProvider>,
  );
}

describe('AuditPage', () => {
  it('lists the trail and names who acted', async () => {
    server.use(
      adminSession(),
      http.get('/api/audit/filters', () => HttpResponse.json(FILTER_OPTIONS)),
      http.get('/api/audit/events', () => HttpResponse.json(EVENTS)),
    );

    renderPage();

    expect(await screen.findByText('1 total')).toBeInTheDocument();
    expect(within(eventsTable()).getByText('tenants')).toBeInTheDocument();
    expect(within(eventsTable()).getByText('Renée Calloway')).toBeInTheDocument();
  });

  it('narrows the request when a filter is chosen', async () => {
    const requested: string[] = [];
    server.use(
      adminSession(),
      http.get('/api/audit/filters', () => HttpResponse.json(FILTER_OPTIONS)),
      http.get('/api/audit/events', ({ request }) => {
        requested.push(new URL(request.url).searchParams.get('entityType') ?? '');
        return HttpResponse.json(EVENTS);
      }),
    );

    renderPage();
    await screen.findByText('1 total');

    await userEvent.selectOptions(screen.getByLabelText('Record type'), 'journal_entries');

    await waitFor(() => expect(requested).toContain('journal_entries'));
  });

  /**
   * The selects are fed by an auxiliary read, and an auxiliary failure is the invisible kind: an
   * empty vocabulary renders as a confident "this org has no record types". It must show the failure
   * and a retry instead.
   */
  it('shows the failure, with its support reference, when the filter vocabulary cannot be read', async () => {
    server.use(
      adminSession(),
      http.get('/api/audit/filters', () => problemResponse()),
      http.get('/api/audit/events', () => HttpResponse.json(EVENTS)),
    );

    renderPage();

    await screen.findByText(READ_FAILURE_COPY);
    expectReadFailure();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
    expect(screen.queryByLabelText('Record type')).not.toBeInTheDocument();
  });

  it('shows the failure, with its support reference, when the trail cannot be read', async () => {
    server.use(
      adminSession(),
      http.get('/api/audit/filters', () => HttpResponse.json(FILTER_OPTIONS)),
      http.get('/api/audit/events', () => problemResponse()),
    );

    renderPage();

    expect(await screen.findByText("Couldn't load the audit log")).toBeInTheDocument();
    expectReadFailure();
  });

  it('blocks the export while the trail has not loaded', async () => {
    server.use(
      adminSession(),
      http.get('/api/audit/filters', () => HttpResponse.json(FILTER_OPTIONS)),
      http.get('/api/audit/events', () => problemResponse()),
    );

    renderPage();

    await screen.findByText("Couldn't load the audit log");
    expect(screen.getByRole('button', { name: 'Export CSV' })).toBeDisabled();
  });

  it('says nothing matched rather than showing an empty table', async () => {
    server.use(
      adminSession(),
      http.get('/api/audit/filters', () => HttpResponse.json(FILTER_OPTIONS)),
      http.get('/api/audit/events', () =>
        HttpResponse.json({ rows: [], total: 0, page: 1, pageSize: 50 }),
      ),
    );

    renderPage();

    expect(await screen.findByText('No matching events')).toBeInTheDocument();
  });

  it('opens the payload drawer for a row', async () => {
    server.use(
      adminSession(),
      http.get('/api/audit/filters', () => HttpResponse.json(FILTER_OPTIONS)),
      http.get('/api/audit/events', () => HttpResponse.json(EVENTS)),
      http.get('/api/audit/events/event-1', () =>
        HttpResponse.json({
          event: EVENTS.rows[0],
          changes: [
            { field: 'ContactEmail', before: null, after: 'new@example.com', redacted: false },
          ],
        }),
      ),
    );

    renderPage();
    await screen.findByText('1 total');
    // The row's focusable control, not the row itself: the Table primitive's click handler is on the
    // <tr>, which no keyboard can reach.
    await userEvent.click(
      within(eventsTable()).getByRole('button', { name: /^View tenants update/ }),
    );

    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText('ContactEmail')).toBeInTheDocument();
    expect(within(dialog).getByText('new@example.com')).toBeInTheDocument();
  });

  /**
   * The endpoint is the enforcement; this is only about not handing a non-admin a generic read
   * failure for a page they were never meant to open. The baseline session handler is logged out,
   * which is a confirmed non-admin.
   */
  it('turns a non-admin away without asking the server for the trail', async () => {
    let asked = false;
    server.use(
      http.get('/api/audit/events', () => {
        asked = true;
        return HttpResponse.json(EVENTS);
      }),
    );

    renderPage();

    expect(await screen.findByText('Admin access required')).toBeInTheDocument();
    expect(asked).toBe(false);
  });
});
