import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import { ImportWizard } from './ImportWizard';

const CSV = 'Date,Description,Amount\n2026-02-01,Interest,100.00\n';

const PREVIEW = {
  rows: [
    {
      statementLineId: 's1',
      date: '2026-02-01',
      description: 'Interest',
      amount: 100,
      kind: 'matched',
      journalLineId: 'jl1',
      candidateAmount: 100,
      candidateDate: '2026-02-01',
      candidateDescription: 'Interest',
    },
  ],
  summary: { matched: 1, suggested: 0, unmatched: 0 },
};

function renderWizard() {
  const onConfirmed = vi.fn();
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  render(
    <QueryClientProvider client={queryClient}>
      <ImportWizard bankAccountId="acct1" onClose={vi.fn()} onConfirmed={onConfirmed} />
    </QueryClientProvider>,
  );
  return { onConfirmed };
}

beforeEach(() => {
  document.body.innerHTML = '';
  vi.clearAllMocks();
});

describe('ImportWizard', () => {
  it('uploads, maps columns, previews matches, and confirms — clearing the matched line', async () => {
    let importBody: { columnMap?: Record<string, unknown> } | undefined;
    let confirmBody: { decisions?: unknown[] } | undefined;
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/banking/banks/:id/mappings', () => HttpResponse.json({ mappings: [] })),
      http.post('/api/banking/banks/:id/imports', async ({ request }) => {
        importBody = (await request.json()) as { columnMap?: Record<string, unknown> };
        return HttpResponse.json({
          importId: 'imp1',
          imported: 1,
          skippedDuplicates: 0,
          errors: [],
        });
      }),
      http.get('/api/banking/imports/:importId/matches', () => HttpResponse.json(PREVIEW)),
      http.post('/api/banking/imports/:importId/confirm', async ({ request }) => {
        confirmBody = (await request.json()) as { decisions?: unknown[] };
        return HttpResponse.json({ cleared: 1, recorded: 1, unmatchedLineIds: [] });
      }),
    );
    const { onConfirmed } = renderWizard();

    // Step 1 — upload (the file is read in-browser).
    const file = new File([CSV], 'statement.csv', { type: 'text/csv' });
    await userEvent.upload(screen.getByLabelText('Statement CSV'), file);

    // Step 2 — map columns from the parsed header.
    await userEvent.selectOptions(await screen.findByLabelText('Date column'), 'Date');
    await userEvent.selectOptions(screen.getByLabelText('Description column'), 'Description');
    await userEvent.selectOptions(screen.getByLabelText('Amount column'), 'Amount');
    await userEvent.click(screen.getByRole('button', { name: 'Preview matches' }));

    // Step 3 — the matched group renders; confirm clears it.
    expect(await screen.findByText('Matched')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Confirm & clear' }));

    await vi.waitFor(() => expect(onConfirmed).toHaveBeenCalled());
    expect(importBody?.columnMap).toMatchObject({
      date: 'Date',
      description: 'Description',
      amount: 'Amount',
    });
    expect(confirmBody?.decisions).toEqual([
      { statementLineId: 's1', journalLineId: 'jl1', kind: 'matched' },
    ]);
  });
});
