import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import { AuditEventDrawer } from './AuditEventDrawer';

const EVENT = {
  id: 'event-1',
  occurredAt: '2026-09-11T14:02:00Z',
  entityType: 'import_rows',
  entityId: 'row-1',
  action: 'insert',
  actorName: 'Renée Calloway',
  actorEmail: 'renee@example.com',
};

function renderDrawer() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <AuditEventDrawer eventId="event-1" onClose={vi.fn()} />
    </QueryClientProvider>,
  );
}

describe('AuditEventDrawer', () => {
  it('shows each recorded field and who changed it', async () => {
    server.use(
      http.get('/api/audit/events/event-1', () =>
        HttpResponse.json({
          event: EVENT,
          changes: [
            { field: 'RowNumber', before: null, after: '3', redacted: false },
            { field: 'RowStatus', before: null, after: 'staged', redacted: false },
          ],
        }),
      ),
    );

    renderDrawer();

    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText('RowNumber')).toBeInTheDocument();
    expect(within(dialog).getByText('staged')).toBeInTheDocument();
    expect(within(dialog).getByText('Renée Calloway')).toBeInTheDocument();
  });

  /**
   * A withheld value has to read as withheld — in words, not only in tone. "You may not read this"
   * and "this was cleared" are different facts about the record, and a blank cell says the second.
   */
  it('labels a withheld value rather than blanking it', async () => {
    server.use(
      http.get('/api/audit/events/event-1', () =>
        HttpResponse.json({
          event: EVENT,
          changes: [
            { field: 'RawJson', before: null, after: '[redacted]', redacted: true },
            { field: 'RowNumber', before: null, after: '3', redacted: false },
          ],
        }),
      ),
    );

    renderDrawer();

    const dialog = await screen.findByRole('dialog');
    const withheld = (await within(dialog).findByText('RawJson')).closest('tr')!;
    expect(within(withheld).getByText('Withheld')).toBeInTheDocument();
    expect(within(withheld).getByText('[redacted]')).toBeInTheDocument();

    // Redaction is per field: the row beside it still reads normally.
    const readable = within(dialog).getByText('RowNumber').closest('tr')!;
    expect(within(readable).queryByText('Withheld')).not.toBeInTheDocument();
  });

  it('says so when an event records no field values', async () => {
    server.use(
      http.get('/api/audit/events/event-1', () =>
        HttpResponse.json({ event: { ...EVENT, entityType: 'account-security' }, changes: [] }),
      ),
    );

    renderDrawer();

    expect(await screen.findByText('No recorded values')).toBeInTheDocument();
  });

  it('reports a failed read with its support reference', async () => {
    server.use(
      http.get('/api/audit/events/event-1', () =>
        HttpResponse.json(
          {
            title: 'internal_error',
            status: 500,
            detail: 'Audit read failed.',
            code: 'internal_error',
            correlationId: 'abcdef01234567890abcdef012345678',
          },
          { status: 500, headers: { 'content-type': 'application/problem+json' } },
        ),
      ),
    );

    renderDrawer();

    expect(await screen.findByText("Couldn't load the event")).toBeInTheDocument();
    // The read wording for a 500, plus the reference. The write wording would claim "Nothing was
    // saved" about a read, which is what a missing kind="read" produces.
    expect(screen.getByText('Something went wrong on our end.')).toBeInTheDocument();
    expect(screen.getByText('Reference: abcdef01234567890abcdef012345678')).toBeInTheDocument();
    expect(screen.queryByText(/Nothing was saved/)).not.toBeInTheDocument();
  });
});
