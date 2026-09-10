import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import { AuditDrawer } from './AuditDrawer';

const ROWS = {
  rows: [
    {
      action: 'insert',
      actorName: 'Dana Whitfield',
      actorEmail: 'dana@example.test',
      occurredAt: '2026-02-01T09:00:00Z',
    },
  ],
};

const REFERENCE = 'd1d2d3d4d1d2d3d4d1d2d3d4d1d2d3d4';

function renderDrawer() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <AuditDrawer entryId="e1" onClose={vi.fn()} />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  document.body.innerHTML = '';
});

describe('AuditDrawer when the audit trail cannot load', () => {
  it('carries the support reference and offers a retry', async () => {
    server.use(
      http.get('/api/accounting/entries/:entryId/audit', () =>
        HttpResponse.json(
          { detail: 'The audit store is unavailable.', correlationId: REFERENCE },
          { status: 503 },
        ),
      ),
    );
    renderDrawer();

    expect(await screen.findByText("Couldn't load the history")).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent('The audit store is unavailable.');
    expect(screen.getByText(`Reference: ${REFERENCE}`)).toBeInTheDocument();

    // "No history yet" on an append-only journal entry is a claim about the audit record itself.
    expect(screen.queryByText('No history yet')).toBeNull();
  });

  it('recovers when the retry succeeds', async () => {
    let attempt = 0;
    server.use(
      http.get('/api/accounting/entries/:entryId/audit', () => {
        attempt += 1;
        return attempt === 1 ? new HttpResponse(null, { status: 500 }) : HttpResponse.json(ROWS);
      }),
    );
    renderDrawer();

    await userEvent.click(await screen.findByRole('button', { name: 'Retry' }));

    expect(await screen.findByText('Dana Whitfield')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
