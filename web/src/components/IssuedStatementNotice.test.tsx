import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import { IssuedStatementNotice, type IssuedCoverageTarget } from './IssuedStatementNotice';

const HARBORVIEW = {
  ownerId: 'owner-1',
  ownerName: 'Harborview Holdings',
  basis: 'accrual',
  propertyId: null,
  propertyAddress: null,
  issuedYear: 2026,
  issuedMonth: 9,
};

const BEACON_SCOPED = {
  ownerId: 'owner-2',
  ownerName: 'Beacon Ridge LLC',
  basis: 'cash',
  propertyId: 'property-9',
  propertyAddress: '72 Beacon Hill Ct',
  issuedYear: 2026,
  issuedMonth: 12,
};

function respondWith(rows: unknown[], seen?: (url: URL) => void) {
  server.use(
    http.get('/api/statements/issued-coverage', ({ request }) => {
      seen?.(new URL(request.url));
      return HttpResponse.json({ rows });
    }),
  );
}

function renderNotice(target: IssuedCoverageTarget, onDismiss?: () => void) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <IssuedStatementNotice target={target} onDismiss={onDismiss} />
    </QueryClientProvider>,
  );
  // The live region is present before the answer arrives — that is what makes it announced.
  return screen.getByRole('status');
}

describe('IssuedStatementNotice', () => {
  it('names the issued statement and the one that will itemize the entry, asking by entry id', async () => {
    let asked: URL | undefined;
    respondWith([HARBORVIEW], (url) => (asked = url));

    const status = renderNotice({ entryIds: ['entry-1'] });

    await vi.waitFor(() =>
      expect(status).toHaveTextContent(
        'Harborview Holdings — the accrual statement for September 2026 was already issued. ' +
          'This entry will appear as a prior-period adjustment on their October 2026 accrual statement.',
      ),
    );
    expect(asked?.searchParams.getAll('entryIds')).toEqual(['entry-1']);
    expect(asked?.searchParams.has('runId')).toBe(false);
  });

  it('includes the property scope and rolls the following month into the next year', async () => {
    respondWith([BEACON_SCOPED]);
    const status = renderNotice({ entryIds: ['entry-1'] });

    await vi.waitFor(() =>
      expect(status).toHaveTextContent(
        'the cash statement for December 2026 (72 Beacon Hill Ct) was already issued. ' +
          'This entry will appear as a prior-period adjustment on their January 2027 cash statement.',
      ),
    );
  });

  it('keeps the live region empty when no issued statement is affected', async () => {
    let answered = false;
    respondWith([], () => (answered = true));
    const status = renderNotice({ entryIds: ['entry-1'] });

    await vi.waitFor(() => expect(answered).toBe(true));
    expect(status).toBeEmptyDOMElement();
  });

  it('says the check failed instead of staying silent', async () => {
    server.use(
      http.get('/api/statements/issued-coverage', () =>
        HttpResponse.json({ code: 'internal_error' }, { status: 500 }),
      ),
    );
    const status = renderNotice({ entryIds: ['entry-1'] });

    await vi.waitFor(() =>
      expect(status).toHaveTextContent(
        "Couldn't check for issued statements affected by this entry.",
      ),
    );
  });

  it('summarizes a run by owner count with the per-statement lines one click away', async () => {
    let asked: URL | undefined;
    respondWith([HARBORVIEW, BEACON_SCOPED], (url) => (asked = url));
    const status = renderNotice({ runId: 'run-1' });

    await vi.waitFor(() =>
      expect(status).toHaveTextContent(
        'Statements already issued for 2 owners; these postings will appear as prior-period adjustments on the statement for the month after each.',
      ),
    );
    expect(asked?.searchParams.get('runId')).toBe('run-1');

    await userEvent.click(within(status).getByText('Show affected statements'));
    const items = within(status).getAllByRole('listitem');
    expect(items).toHaveLength(2);
    expect(items[0]).toHaveTextContent('These postings will appear as prior-period adjustments');
  });

  it('shows a single run statement as its own line', async () => {
    respondWith([HARBORVIEW]);
    const status = renderNotice({ runId: 'run-1' });

    await vi.waitFor(() =>
      expect(status).toHaveTextContent(
        'Harborview Holdings — the accrual statement for September 2026',
      ),
    );
    expect(within(status).queryByText('Show affected statements')).not.toBeInTheDocument();
  });

  it('is dismissible and never requires it', async () => {
    const onDismiss = vi.fn();
    respondWith([HARBORVIEW]);
    const status = renderNotice({ entryIds: ['entry-1'] }, onDismiss);

    await userEvent.click(await within(status).findByRole('button', { name: 'Dismiss' }));
    expect(onDismiss).toHaveBeenCalledOnce();
  });
});
