import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse, delay } from 'msw';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { OwnerPortalStatements, OwnerPortalSummary } from '@/api';
import { server } from '@/test/mocks/server';
import { OwnerPortalPage } from './OwnerPortalPage';

const REFERENCE = '1234567890abcdef1234567890abcdef';
const STATEMENT_ID = '0192a4b0-0000-7000-8000-00000000a001';
const OLDER_ID = '0192a4b0-0000-7000-8000-00000000a002';

const SUMMARY: OwnerPortalSummary = {
  ownerName: 'Owner A',
  balance: 1250.5,
  basis: 'cash',
  disbursements: [
    { date: '2026-08-05', amount: 900, isVoided: true, isReversal: false },
    { date: '2026-08-06', amount: -900, isVoided: false, isReversal: true },
  ],
  activity: [
    {
      date: '2026-08-01',
      category: 'Rent collected',
      propertyAddress: '12 Oak St',
      amount: 1400,
      balance: 1400,
      isVoided: false,
      isReversal: false,
    },
    {
      date: '2026-08-02',
      category: 'Management fee',
      propertyAddress: null,
      amount: -149.5,
      balance: 1250.5,
      isVoided: false,
      isReversal: false,
    },
  ],
};

const STATEMENTS: OwnerPortalStatements = {
  statements: [
    {
      id: STATEMENT_ID,
      periodYear: 2026,
      periodMonth: 8,
      basis: 'cash',
      scope: 'All properties',
      endingBalance: 1250.5,
      issuedAt: '2026-09-02T14:00:00Z',
    },
    {
      id: OLDER_ID,
      periodYear: 2026,
      periodMonth: 7,
      basis: null,
      scope: '12 Oak St',
      endingBalance: null,
      issuedAt: '2026-08-03T14:00:00Z',
    },
  ],
};

function stub(
  summary: OwnerPortalSummary = SUMMARY,
  statements: OwnerPortalStatements = STATEMENTS,
) {
  server.use(
    http.get('/api/portal/owner/summary', () => HttpResponse.json(summary)),
    http.get('/api/portal/owner/statements', () => HttpResponse.json(statements)),
  );
}

function show() {
  render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter>
        <OwnerPortalPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('owner portal reads', () => {
  it('shows loading before any balance is asserted', async () => {
    server.use(
      http.get('/api/portal/owner/summary', async () => {
        await delay('infinite');
        return HttpResponse.json({});
      }),
      http.get('/api/portal/owner/statements', async () => {
        await delay('infinite');
        return HttpResponse.json({});
      }),
    );
    show();
    const loading = await screen.findAllByRole('status');
    expect(loading.map((el) => el.textContent)).toEqual([
      'Loading your trust balance…',
      'Loading statements…',
    ]);
    expect(screen.queryByText('Balance held in trust')).not.toBeInTheDocument();
    expect(screen.queryByText('No statements issued yet')).not.toBeInTheDocument();
  });

  it('renders the balance as held in trust with its basis, and every section', async () => {
    stub();
    show();
    expect(await screen.findByRole('heading', { name: 'Owner A' })).toBeInTheDocument();
    expect(screen.getByText('Balance held in trust')).toBeInTheDocument();
    expect(screen.getByText(/Cash basis\./)).toBeInTheDocument();
    expect(screen.getByText(/security deposits are held separately/)).toBeInTheDocument();
    const headline = screen.getAllByText('$1,250.50')[0];
    expect(headline).toHaveClass('pf-money');

    const statements = await screen.findByRole('table', { name: /Issued statements/ });
    const rows = within(statements).getAllByRole('row').slice(1);
    expect(within(rows[0]!).getByText('August 2026')).toBeInTheDocument();
    expect(within(rows[0]!).getByText('All properties')).toBeInTheDocument();
    expect(within(rows[0]!).getByText('2026-09-02')).toBeInTheDocument();
    expect(within(rows[1]!).getByText('July 2026')).toBeInTheDocument();
    expect(within(rows[1]!).getAllByText('Not recorded')).toHaveLength(2);
    // Every issued document is listed as issued — never labelled current or superseded.
    expect(within(statements).queryByText(/current|supersed|amend/i)).not.toBeInTheDocument();

    const disbursements = screen.getByRole('table', { name: 'Money paid out to you' });
    expect(within(disbursements).getByText('Voided')).toBeInTheDocument();
    expect(within(disbursements).getByText('Reversal')).toBeInTheDocument();
    expect(within(disbursements).getByText('−$900.00')).toHaveClass('pf-money');

    const activity = screen.getByRole('table', { name: 'Trust activity' });
    expect(within(activity).getByText('12 Oak St')).toBeInTheDocument();
    expect(within(activity).getByText('Not property-specific')).toBeInTheDocument();
    expect(within(activity).getByText('−$149.50')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Account security' })).toHaveAttribute(
      'href',
      '/account/security',
    );
    expect(screen.getByRole('button', { name: 'Sign out everywhere' })).toBeEnabled();
  });

  it('says so when no statements or disbursements exist yet', async () => {
    stub({ ...SUMMARY, disbursements: [], activity: [] }, { statements: [] });
    show();
    expect(await screen.findByText('No statements issued yet')).toBeInTheDocument();
    expect(await screen.findByText('No disbursements yet')).toBeInTheDocument();
    expect(screen.getByText('No trust activity yet')).toBeInTheDocument();
    expect(screen.getByText('Balance held in trust')).toBeInTheDocument();
  });

  it('keeps a failed balance read distinct and preserves the support reference', async () => {
    server.use(
      http.get('/api/portal/owner/summary', () =>
        HttpResponse.json(
          {
            code: 'read_failed',
            detail: 'Balance temporarily unavailable.',
            correlationId: REFERENCE,
          },
          { status: 500 },
        ),
      ),
      http.get('/api/portal/owner/statements', () => HttpResponse.json(STATEMENTS)),
    );
    show();
    expect(await screen.findByText('Couldn’t load your trust balance')).toBeInTheDocument();
    expect(screen.getByText('Balance temporarily unavailable.')).toBeInTheDocument();
    expect(screen.getByText(`Reference: ${REFERENCE}`)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /retry/i })).toBeInTheDocument();
    expect(screen.queryByText('Balance held in trust')).not.toBeInTheDocument();
    expect(screen.queryByText('No disbursements yet')).not.toBeInTheDocument();
    // The independent statements read still answers.
    expect(await screen.findByText('August 2026')).toBeInTheDocument();
  });

  it('keeps a failed statements read from reading as “no statements”', async () => {
    server.use(
      http.get('/api/portal/owner/summary', () => HttpResponse.json(SUMMARY)),
      http.get('/api/portal/owner/statements', () =>
        HttpResponse.json(
          { code: 'read_failed', detail: 'Statements unavailable.', correlationId: REFERENCE },
          { status: 500 },
        ),
      ),
    );
    show();
    expect(await screen.findByText('Couldn’t load your statements')).toBeInTheDocument();
    expect(screen.getByText('Statements unavailable.')).toBeInTheDocument();
    expect(screen.getByText(`Reference: ${REFERENCE}`)).toBeInTheDocument();
    expect(screen.queryByText('No statements issued yet')).not.toBeInTheDocument();
  });

  it('names denied owner access once, without empty states', async () => {
    server.use(
      http.get('/api/portal/owner/summary', () => new HttpResponse(null, { status: 403 })),
      http.get('/api/portal/owner/statements', () => new HttpResponse(null, { status: 403 })),
    );
    show();
    expect(await screen.findByText('Owner access unavailable')).toBeInTheDocument();
    expect(screen.getAllByText('Owner access unavailable')).toHaveLength(1);
    expect(screen.queryByText('No statements issued yet')).not.toBeInTheDocument();
    expect(screen.queryByText('Balance held in trust')).not.toBeInTheDocument();
  });

  it('offers sign-in, not retry, for an expired session', async () => {
    const expired = () =>
      HttpResponse.json({ code: 'not_authenticated', correlationId: REFERENCE }, { status: 401 });
    server.use(
      http.get('/api/portal/owner/summary', expired),
      http.get('/api/portal/owner/statements', expired),
    );
    show();
    expect(await screen.findByRole('link', { name: /sign in/i })).toHaveAttribute('href', '/login');
    expect(screen.getByText(/You have been signed out/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /retry/i })).not.toBeInTheDocument();
  });
});

describe('opening a statement PDF', () => {
  const createObjectURL = vi.fn(() => 'blob:statement');
  let requested: string[];

  beforeEach(() => {
    requested = [];
    createObjectURL.mockClear();
    globalThis.URL.createObjectURL = createObjectURL;
    globalThis.URL.revokeObjectURL = vi.fn();
    stub();
  });
  afterEach(() => vi.restoreAllMocks());

  function pdf(respond: () => Response) {
    server.use(
      http.get('/api/portal/owner/statements/:artifactId/pdf', ({ request }) => {
        requested.push(new URL(request.url).pathname);
        return respond();
      }),
    );
  }

  async function openFirst() {
    const user = userEvent.setup();
    const button = await screen.findByRole('button', {
      name: 'Open PDF: August 2026 statement, All properties, issued 2026-09-02',
    });
    // Keyboard path: focus the action and press Enter.
    button.focus();
    await user.keyboard('{Enter}');
  }

  it('fetches that statement’s document by its own id and opens it', async () => {
    pdf(
      () =>
        new HttpResponse(new Blob(['%PDF-1.7']), {
          headers: { 'Content-Type': 'application/pdf' },
        }),
    );
    const open = vi.spyOn(window, 'open').mockReturnValue({ opener: null } as Window);
    show();
    await openFirst();
    await vi.waitFor(() => expect(open).toHaveBeenCalledWith('blob:statement', '_blank'));
    expect(requested).toEqual([`/api/portal/owner/statements/${STATEMENT_ID}/pdf`]);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('announces an unavailable document in the page, with no retry', async () => {
    pdf(() =>
      HttpResponse.json(
        {
          code: 'statement_document_unavailable',
          detail:
            'This statement was issued, but its document cannot be retrieved right now. Contact your property manager.',
          correlationId: REFERENCE,
        },
        { status: 503 },
      ),
    );
    const open = vi.spyOn(window, 'open');
    show();
    await openFirst();
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('its document cannot be retrieved right now');
    expect(alert).toHaveTextContent(`Reference: ${REFERENCE}`);
    expect(
      screen.getByText('Statement document unavailable — August 2026 statement, All properties'),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /retry/i })).not.toBeInTheDocument();
    expect(open).not.toHaveBeenCalled();
    expect(createObjectURL).not.toHaveBeenCalled();
    expect(requested).toEqual([`/api/portal/owner/statements/${STATEMENT_ID}/pdf`]);
  });

  it('switches on the problem code, not the status', async () => {
    pdf(() =>
      HttpResponse.json({ code: 'internal_error', correlationId: REFERENCE }, { status: 503 }),
    );
    show();
    await openFirst();
    expect(
      await screen.findByText('Couldn’t open the August 2026 statement, All properties'),
    ).toBeInTheDocument();
    expect(screen.queryByText(/Statement document unavailable/)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /retry/i })).toBeInTheDocument();
  });

  it('reports a statement that is gone without offering a retry', async () => {
    pdf(() => new HttpResponse(null, { status: 404 }));
    show();
    await openFirst();
    expect(await screen.findByRole('alert')).toHaveTextContent('This statement is not available.');
    expect(screen.queryByRole('button', { name: /retry/i })).not.toBeInTheDocument();
  });

  it('offers sign-in when the session expired before the document was fetched', async () => {
    pdf(() => HttpResponse.json({ code: 'not_authenticated' }, { status: 401 }));
    show();
    await openFirst();
    expect(await screen.findByRole('alert')).toHaveTextContent('You have been signed out');
    expect(screen.getByRole('link', { name: /sign in/i })).toHaveAttribute('href', '/login');
  });
});
