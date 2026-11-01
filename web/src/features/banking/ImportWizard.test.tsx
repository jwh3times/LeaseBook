import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import type { ImportResult } from './banking';
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

const EMPTY_PREVIEW = { rows: [], summary: { matched: 0, suggested: 0, unmatched: 0 } };

/** Takes the wizard through upload and mapping to the preview of an import with these results. */
async function previewImport(result: {
  imported: number;
  broughtBack?: number;
  skippedDuplicates?: number;
  errors: ImportResult['errors'];
  preview: typeof PREVIEW | typeof EMPTY_PREVIEW;
}) {
  server.use(
    http.get('/api/banking/banks/:id/mappings', () => HttpResponse.json({ mappings: [] })),
    http.post('/api/banking/banks/:id/imports', () =>
      HttpResponse.json({
        importId: 'imp1',
        imported: result.imported,
        broughtBack: result.broughtBack ?? 0,
        skippedDuplicates: result.skippedDuplicates ?? 0,
        errors: result.errors,
      }),
    ),
    http.get('/api/banking/imports/:id/matches', () => HttpResponse.json(result.preview)),
  );
  renderWizard();
  await mapAndPreview();
  await screen.findByText(/^Imported/);
}

/** Uploads the CSV, maps its columns and asks for the preview. */
async function mapAndPreview() {
  await userEvent.upload(
    screen.getByLabelText('Statement CSV'),
    new File([CSV], 'statement.csv', { type: 'text/csv' }),
  );
  await screen.findByText(/no saved mappings yet/i);
  await userEvent.selectOptions(screen.getByLabelText('Date column'), 'Date');
  await userEvent.selectOptions(screen.getByLabelText('Description column'), 'Description');
  await userEvent.selectOptions(screen.getByLabelText('Amount column'), 'Amount');
  await userEvent.click(screen.getByRole('button', { name: 'Preview matches' }));
}

describe('ImportWizard', () => {
  // #671: a line stored by an import stays reachable, whichever way the operator left its preview.
  describe('lines from an earlier import', () => {
    it('counts them apart from the lines this file added and the duplicates it skipped', async () => {
      await previewImport({
        imported: 1,
        broughtBack: 2,
        skippedDuplicates: 1,
        errors: [],
        preview: PREVIEW,
      });

      expect(
        screen.getByText(
          'Imported 1 line · 2 lines from an earlier import not yet confirmed · 1 duplicate skipped',
        ),
      ).toBeInTheDocument();
    });

    it('says one in the singular', async () => {
      await previewImport({ imported: 0, broughtBack: 1, errors: [], preview: PREVIEW });

      expect(
        screen.getByText('Imported 0 lines · 1 line from an earlier import not yet confirmed'),
      ).toBeInTheDocument();
      expect(screen.getByRole('button', { name: /confirm/i })).toBeEnabled();
    });

    it('offers the line again after the first preview could not be read', async () => {
      let imports = 0;
      let reads = 0;
      server.use(
        http.get('/api/banking/banks/:id/mappings', () => HttpResponse.json({ mappings: [] })),
        http.post('/api/banking/banks/:id/imports', () => {
          imports += 1;
          return HttpResponse.json({
            importId: `imp${imports}`,
            imported: imports === 1 ? 1 : 0,
            broughtBack: imports === 1 ? 0 : 1,
            skippedDuplicates: 0,
            errors: [],
          });
        }),
        http.get('/api/banking/imports/:id/matches', () => {
          reads += 1;
          return reads === 1
            ? HttpResponse.json({ code: 'unavailable', detail: 'Try again.' }, { status: 503 })
            : HttpResponse.json(PREVIEW);
        }),
      );
      renderWizard();
      await mapAndPreview();

      await screen.findByRole('alert');
      expect(screen.queryByText(/^Imported/)).not.toBeInTheDocument();

      await userEvent.click(screen.getByRole('button', { name: 'Preview matches' }));

      expect(
        await screen.findByText(
          'Imported 0 lines · 1 line from an earlier import not yet confirmed',
        ),
      ).toBeInTheDocument();
      expect(screen.getByText('Interest')).toBeInTheDocument();
      expect(screen.getByRole('button', { name: /confirm/i })).toBeEnabled();
    });
  });

  // #667: the preview used to say only "1 row error(s)", so the operator had to hunt for the row.
  describe('rows that were not imported', () => {
    it('lists each with its file line and reason, and says to correct the file', async () => {
      await previewImport({
        imported: 1,
        errors: [
          { lineNumber: 3, message: 'amount is empty.' },
          { lineNumber: 7, message: "date 'Total' is not in a recognized format." },
        ],
        preview: PREVIEW,
      });

      const notice = screen.getByRole('alert');
      expect(notice).toHaveTextContent('2 rows were not imported.');
      // The order no longer matters: the corrected file brings back any line not yet confirmed (#671).
      expect(notice).toHaveTextContent('Correct the file and import it again.');
      expect(notice).not.toHaveTextContent(/confirm/i);

      const list = screen.getByRole('region', { name: 'Rows not imported' });
      expect(
        within(list)
          .getAllByRole('columnheader')
          .map((th) => th.textContent),
      ).toEqual(['Line', 'Reason']);
      expect(
        within(list)
          .getAllByRole('cell')
          .map((td) => td.textContent),
      ).toEqual(['3', 'amount is empty.', '7', "date 'Total' is not in a recognized format."]);

      expect(screen.getByRole('button', { name: /confirm/i })).toBeEnabled();
    });

    it('lets the keyboard reach the list, which scrolls', async () => {
      await previewImport({
        imported: 1,
        errors: [{ lineNumber: 3, message: 'amount is empty.' }],
        preview: PREVIEW,
      });

      const list = screen.getByRole('region', { name: 'Rows not imported' });
      await userEvent.tab(); // the dialog's close button
      await userEvent.tab();
      expect(list).toHaveFocus();
    });

    it('says to correct the file when the readable rows were all duplicates', async () => {
      await previewImport({
        imported: 0,
        skippedDuplicates: 2,
        errors: [{ lineNumber: 4, message: 'amount is empty.' }],
        preview: EMPTY_PREVIEW,
      });

      expect(screen.getByRole('alert')).toHaveTextContent('Correct the file and import it again.');
      expect(screen.getByRole('button', { name: /confirm/i })).toBeDisabled();
    });

    it('says one row in the singular', async () => {
      await previewImport({
        imported: 1,
        errors: [{ lineNumber: 3, message: 'amount is empty.' }],
        preview: PREVIEW,
      });

      expect(screen.getByRole('alert')).toHaveTextContent('1 row was not imported.');
    });

    it('says to correct the file when every row failed, with nothing to confirm', async () => {
      await previewImport({
        imported: 0,
        errors: [{ lineNumber: 2, message: 'amount is empty.' }],
        preview: EMPTY_PREVIEW,
      });

      const notice = screen.getByRole('alert');
      expect(notice).toHaveTextContent('1 row was not imported.');
      expect(notice).toHaveTextContent('Correct the file and import it again.');
      expect(notice).not.toHaveTextContent(/confirm/i);
      expect(
        within(screen.getByRole('region', { name: 'Rows not imported' })).getByText(
          'amount is empty.',
        ),
      ).toBeInTheDocument();
      expect(screen.getByRole('button', { name: /confirm/i })).toBeDisabled();
    });

    it('shows a cell value quoted in a reason as literal text', async () => {
      const reason = "date '<b>x</b>' is not in a recognized format.";
      await previewImport({
        imported: 1,
        errors: [{ lineNumber: 2, message: reason }],
        preview: PREVIEW,
      });

      const list = screen.getByRole('region', { name: 'Rows not imported' });
      expect(within(list).getByText(reason)).toBeInTheDocument();
      expect(list.querySelector('b')).toBeNull();
    });

    it('shows no list when every row was read', async () => {
      await previewImport({ imported: 1, errors: [], preview: PREVIEW });

      expect(screen.getByText('Imported 1 line')).toBeInTheDocument();
      expect(screen.queryByRole('alert')).not.toBeInTheDocument();
      expect(screen.queryByRole('region', { name: 'Rows not imported' })).not.toBeInTheDocument();
      expect(screen.getByRole('button', { name: /confirm/i })).toBeEnabled();
    });
  });

  it('shows a confirmed empty saved-mapping result', async () => {
    server.use(
      http.get('/api/banking/banks/:id/mappings', () => HttpResponse.json({ mappings: [] })),
    );
    renderWizard();
    await userEvent.upload(
      screen.getByLabelText('Statement CSV'),
      new File([CSV], 'statement.csv', { type: 'text/csv' }),
    );
    expect(await screen.findByText(/no saved mappings yet/i)).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('reports unavailable saved mappings while allowing manual column mapping', async () => {
    server.use(
      http.get('/api/banking/banks/:id/mappings', () =>
        HttpResponse.json({ detail: 'Saved mappings unavailable.' }, { status: 503 }),
      ),
    );
    renderWizard();
    await userEvent.upload(
      screen.getByLabelText('Statement CSV'),
      new File([CSV], 'statement.csv', { type: 'text/csv' }),
    );
    expect(await screen.findByRole('alert')).toHaveTextContent('Saved mappings unavailable.');
    expect(screen.queryByText(/no saved mappings/i)).not.toBeInTheDocument();
    await userEvent.selectOptions(screen.getByLabelText('Date column'), 'Date');
    await userEvent.selectOptions(screen.getByLabelText('Description column'), 'Description');
    await userEvent.selectOptions(screen.getByLabelText('Amount column'), 'Amount');
    expect(screen.getByRole('button', { name: 'Preview matches' })).toBeEnabled();
  });

  // #645: a saved mapping naming a column this file lacks used to be applied whole and submitted, and
  // the server read the absent amount column as 0.00 on every row.
  it('names the saved-mapping columns this file lacks and blocks the import until they are mapped', async () => {
    let posted = false;
    server.use(
      http.get('/api/banking/banks/:id/mappings', () =>
        HttpResponse.json({
          mappings: [
            {
              id: 'm1',
              name: 'Old bank export',
              columnMap: { date: 'Date', description: 'Memo', amount: 'Value' },
            },
          ],
        }),
      ),
      http.post('/api/banking/banks/:id/imports', () => {
        posted = true;
        return HttpResponse.json({
          importId: 'imp1',
          imported: 1,
          skippedDuplicates: 0,
          errors: [],
        });
      }),
    );
    renderWizard();
    await userEvent.upload(
      screen.getByLabelText('Statement CSV'),
      new File([CSV], 'statement.csv', { type: 'text/csv' }),
    );

    await userEvent.selectOptions(await screen.findByLabelText('Saved mapping'), 'm1');

    const notice = screen.getByRole('status', { name: 'Saved mapping does not fit this file' });
    expect(notice).toHaveTextContent('Memo');
    expect(notice).toHaveTextContent('Value');
    expect(screen.getByLabelText('Date column')).toHaveValue('Date');
    expect(screen.getByLabelText('Description column')).toHaveValue('');
    expect(screen.getByLabelText('Amount column')).toHaveValue('');
    const preview = screen.getByRole('button', { name: 'Preview matches' });
    expect(preview).toBeDisabled();

    await userEvent.selectOptions(screen.getByLabelText('Description column'), 'Description');
    await userEvent.selectOptions(screen.getByLabelText('Amount column'), 'Amount');
    expect(preview).toBeEnabled();
    expect(
      screen.queryByRole('status', { name: 'Saved mapping does not fit this file' }),
    ).not.toBeInTheDocument();
    expect(posted).toBe(false);
  });

  // One side of a debit/credit pair is enough for the map to be valid, so a saved pair whose debit
  // column the file lacks must still block: sent with debit: null, every withdrawal imports as 0.00.
  it('blocks a saved debit/credit mapping until the side this file lacks is mapped', async () => {
    const PAIR_CSV = 'Date,Description,Withdrawal,In\n2026-02-01,Fee,25.00,\n';
    server.use(
      http.get('/api/banking/banks/:id/mappings', () =>
        HttpResponse.json({
          mappings: [
            {
              id: 'm2',
              name: 'Pair export',
              columnMap: { date: 'Date', description: 'Description', debit: 'Out', credit: 'In' },
            },
          ],
        }),
      ),
    );
    renderWizard();
    await userEvent.upload(
      screen.getByLabelText('Statement CSV'),
      new File([PAIR_CSV], 'statement.csv', { type: 'text/csv' }),
    );

    await userEvent.selectOptions(await screen.findByLabelText('Saved mapping'), 'm2');

    expect(
      screen.getByRole('status', { name: 'Saved mapping does not fit this file' }),
    ).toHaveTextContent('Out');
    expect(screen.getByLabelText('Credit column')).toHaveValue('In');
    const preview = screen.getByRole('button', { name: 'Preview matches' });
    expect(preview).toBeDisabled();

    await userEvent.selectOptions(screen.getByLabelText('Debit column'), 'Withdrawal');
    expect(preview).toBeEnabled();
  });

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

  // The match preview is a read, and reads used to throw `new Error('Failed to load the match
  // preview')` — discarding the ProblemDetails the host had already sent. `unwrap` carries them, so
  // the operator sees what actually went wrong plus the reference support needs (ADR-025).
  it('surfaces the server detail and support reference when the match preview fails', async () => {
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/banking/banks/:id/mappings', () => HttpResponse.json({ mappings: [] })),
      http.post('/api/banking/banks/:id/imports', () =>
        HttpResponse.json({ importId: 'imp1', imported: 1, skippedDuplicates: 0, errors: [] }),
      ),
      http.get('/api/banking/imports/:importId/matches', () =>
        HttpResponse.json(
          {
            title: 'internal_error',
            detail: 'The matching index is rebuilding. Try again shortly.',
            correlationId: 'op-4821',
          },
          { status: 500 },
        ),
      ),
    );
    renderWizard();

    const file = new File([CSV], 'statement.csv', { type: 'text/csv' });
    await userEvent.upload(screen.getByLabelText('Statement CSV'), file);
    await userEvent.selectOptions(await screen.findByLabelText('Date column'), 'Date');
    await userEvent.selectOptions(screen.getByLabelText('Description column'), 'Description');
    await userEvent.selectOptions(screen.getByLabelText('Amount column'), 'Amount');
    await userEvent.click(screen.getByRole('button', { name: 'Preview matches' }));

    const alert = await screen.findByRole('alert');
    // `internal_error` has its own copy in ApiErrorNotice; the reference is what read paths lacked.
    expect(alert).toHaveTextContent(/Nothing was saved/i);
    expect(alert).toHaveTextContent('Reference: op-4821');
  });

  // One payout is one deposit at the bank and several bank lines in the register (ADR-053).
  it('shows a payout match as one group and confirms it as a group, never line by line', async () => {
    let confirmBody: { decisions?: unknown[] } | undefined;
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/banking/banks/:id/mappings', () => HttpResponse.json({ mappings: [] })),
      http.post('/api/banking/banks/:id/imports', () =>
        HttpResponse.json({ importId: 'imp1', imported: 2, skippedDuplicates: 0, errors: [] }),
      ),
      http.get('/api/banking/imports/:importId/matches', () =>
        HttpResponse.json({
          rows: [
            {
              statementLineId: 's1',
              date: '2026-02-06',
              description: 'PROCESSOR PAYOUT',
              amount: 1492.5,
              kind: 'matched',
              journalLineId: null,
              candidateAmount: 1492.5,
              candidateDate: '2026-02-06',
              candidateDescription: 'Payout po_1',
              groupRef: 'po_1',
              groupLines: [
                { journalLineId: 'jl1', date: '2026-02-06', amount: 1000, description: 'Card' },
                { journalLineId: 'jl2', date: '2026-02-06', amount: 500, description: 'Card' },
                { journalLineId: 'jl3', date: '2026-02-06', amount: -7.5, description: 'Fee' },
              ],
            },
            ...PREVIEW.rows,
          ],
          summary: { matched: 2, suggested: 0, unmatched: 0 },
        }),
      ),
      http.post('/api/banking/imports/:importId/confirm', async ({ request }) => {
        confirmBody = (await request.json()) as { decisions?: unknown[] };
        return HttpResponse.json({ cleared: 4, recorded: 2, unmatchedLineIds: [] });
      }),
    );
    const { onConfirmed } = renderWizard();
    await userEvent.upload(
      screen.getByLabelText('Statement CSV'),
      new File([CSV], 'statement.csv', { type: 'text/csv' }),
    );
    await userEvent.selectOptions(await screen.findByLabelText('Date column'), 'Date');
    await userEvent.selectOptions(screen.getByLabelText('Description column'), 'Description');
    await userEvent.selectOptions(screen.getByLabelText('Amount column'), 'Amount');
    await userEvent.click(screen.getByRole('button', { name: 'Preview matches' }));

    expect(
      await screen.findByText('Payout po_1 — 3 bank lines, cleared together'),
    ).toBeInTheDocument();
    expect(
      within(screen.getByRole('list', { name: 'Bank lines of payout po_1' })).getAllByRole(
        'listitem',
      ),
    ).toHaveLength(3);

    await userEvent.click(screen.getByRole('button', { name: 'Confirm & clear' }));
    await vi.waitFor(() => expect(onConfirmed).toHaveBeenCalled());
    expect(confirmBody?.decisions).toEqual([
      { statementLineId: 's1', journalLineId: null, kind: 'matched', groupRef: 'po_1' },
      { statementLineId: 's1', journalLineId: 'jl1', kind: 'matched' },
    ]);
  });
});
