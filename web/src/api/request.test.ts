import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import {
  getApiDashboard,
  getApiPortalOwnerStatementsByArtifactIdPdf,
  getApiReportsByIdCsv,
} from './generated';
import { download, openDocument, unwrap } from './request';
import type { ApiError } from './apiError';

describe('unwrap', () => {
  it('returns the body on success', async () => {
    server.use(http.get('/api/dashboard', () => HttpResponse.json({ asOf: '2026-08-20' })));

    await expect(unwrap(getApiDashboard(), 'Failed to load the dashboard')).resolves.toMatchObject({
      asOf: '2026-08-20',
    });
  });

  // The point of the shared helper: read failures used to throw `new Error('<literal>')`, which
  // discarded the code and the support reference the server had already sent.
  it('carries the server detail, code and correlationId onto a failed read', async () => {
    server.use(
      http.get('/api/dashboard', () =>
        HttpResponse.json(
          {
            title: 'internal_error',
            detail: 'The report store is unavailable.',
            correlationId: 'op-77',
          },
          { status: 500 },
        ),
      ),
    );

    await expect(unwrap(getApiDashboard(), 'Failed to load the dashboard')).rejects.toMatchObject({
      code: 'internal_error',
      message: 'The report store is unavailable.',
      correlationId: 'op-77',
      status: 500,
    });
  });

  it('falls back to the call site wording when the body explains nothing', async () => {
    server.use(http.get('/api/dashboard', () => new HttpResponse(null, { status: 503 })));

    await expect(unwrap(getApiDashboard(), 'Failed to load the dashboard')).rejects.toMatchObject({
      message: 'Failed to load the dashboard',
      status: 503,
    });
  });

  it('prefers the first validation entry over the fallback', async () => {
    server.use(
      http.get('/api/dashboard', () =>
        HttpResponse.json({ errors: { Year: ['Year must be 2000 or later.'] } }, { status: 400 }),
      ),
    );

    await expect(unwrap(getApiDashboard(), 'Failed to load the dashboard')).rejects.toMatchObject({
      message: 'Year must be 2000 or later.',
    });
  });
});

describe('download', () => {
  const createObjectURL = vi.fn(() => 'blob:test');
  const revokeObjectURL = vi.fn();

  beforeEach(() => {
    document.body.innerHTML = '';
    createObjectURL.mockClear();
    revokeObjectURL.mockClear();
    globalThis.URL.createObjectURL = createObjectURL;
    globalThis.URL.revokeObjectURL = revokeObjectURL;
  });

  it('names the file, clicks the anchor, and revokes the object URL', async () => {
    server.use(
      http.get(
        '/api/reports/:id/csv',
        () =>
          new HttpResponse(new Blob(['a,b\n1,2\n']), { headers: { 'Content-Type': 'text/csv' } }),
      ),
    );
    const clicked: string[] = [];
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      clicked.push(this.download);
    });

    try {
      await download(
        () => getApiReportsByIdCsv({ path: { id: 'trust-ledger' }, parseAs: 'blob' }),
        'report-trust-ledger.csv',
      );
    } finally {
      click.mockRestore();
    }

    expect(clicked).toEqual(['report-trust-ledger.csv']);
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:test');
    // The anchor is removed again — a download must not leave nodes behind.
    expect(document.querySelector('a')).toBeNull();
  });

  it('maps a failed download to an ApiError instead of downloading the error body', async () => {
    server.use(
      http.get('/api/reports/:id/csv', () =>
        HttpResponse.json({ title: 'period_not_closed', correlationId: 'op-9' }, { status: 422 }),
      ),
    );

    let err: ApiError | undefined;
    try {
      await download(
        () => getApiReportsByIdCsv({ path: { id: 'trust-ledger' }, parseAs: 'blob' }),
        'report-trust-ledger.csv',
        'Failed to export the report',
      );
    } catch (e) {
      err = e as ApiError;
    }

    expect(err).toMatchObject({ code: 'period_not_closed', correlationId: 'op-9', status: 422 });
    expect(createObjectURL).not.toHaveBeenCalled();
  });
});

describe('openDocument', () => {
  const createObjectURL = vi.fn(() => 'blob:statement');
  const revokeObjectURL = vi.fn();
  const artifactId = '0192a4b0-0000-7000-8000-000000000001';
  const pdf = () =>
    getApiPortalOwnerStatementsByArtifactIdPdf({ path: { artifactId }, parseAs: 'blob' });

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['setTimeout'] });
    document.body.innerHTML = '';
    createObjectURL.mockClear();
    revokeObjectURL.mockClear();
    globalThis.URL.createObjectURL = createObjectURL;
    globalThis.URL.revokeObjectURL = revokeObjectURL;
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('opens the fetched PDF in a new tab and keeps its URL alive while the tab loads it', async () => {
    const requested: string[] = [];
    server.use(
      http.get('/api/portal/owner/statements/:artifactId/pdf', ({ request }) => {
        requested.push(new URL(request.url).pathname);
        return new HttpResponse(new Blob(['%PDF-1.7']), {
          headers: { 'Content-Type': 'application/pdf' },
        });
      }),
    );
    const tab = { opener: {} as unknown } as Window;
    const open = vi.spyOn(window, 'open').mockReturnValue(tab);

    await openDocument(pdf, 'statement.pdf');

    expect(requested).toEqual([`/api/portal/owner/statements/${artifactId}/pdf`]);
    expect(open).toHaveBeenCalledWith('blob:statement', '_blank');
    expect((createObjectURL.mock.calls[0] as unknown as [Blob])[0].type).toBe('application/pdf');
    expect(tab.opener).toBeNull();
    expect(revokeObjectURL).not.toHaveBeenCalled();
    vi.runAllTimers();
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:statement');
  });

  it('falls back to a download when the browser blocks the tab', async () => {
    server.use(
      http.get(
        '/api/portal/owner/statements/:artifactId/pdf',
        () =>
          new HttpResponse(new Blob(['%PDF-1.7']), {
            headers: { 'Content-Type': 'application/pdf' },
          }),
      ),
    );
    vi.spyOn(window, 'open').mockReturnValue(null);
    const clicked: string[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      clicked.push(this.download);
    });

    await openDocument(pdf, 'statement.pdf');

    expect(clicked).toEqual(['statement.pdf']);
  });

  it('throws the problem code for an unavailable document and opens nothing', async () => {
    server.use(
      http.get('/api/portal/owner/statements/:artifactId/pdf', () =>
        HttpResponse.json(
          {
            code: 'statement_document_unavailable',
            detail: 'This statement was issued, but its document cannot be retrieved right now.',
            correlationId: 'abc123',
          },
          { status: 503 },
        ),
      ),
    );
    const open = vi.spyOn(window, 'open');

    await expect(openDocument(pdf, 'statement.pdf')).rejects.toMatchObject({
      code: 'statement_document_unavailable',
      correlationId: 'abc123',
      status: 503,
    });
    expect(open).not.toHaveBeenCalled();
    expect(createObjectURL).not.toHaveBeenCalled();
  });

  it('maps a bare 404 to the caller fallback', async () => {
    server.use(
      http.get(
        '/api/portal/owner/statements/:artifactId/pdf',
        () => new HttpResponse(null, { status: 404 }),
      ),
    );

    await expect(
      openDocument(pdf, 'statement.pdf', 'This statement is not available.'),
    ).rejects.toMatchObject({ message: 'This statement is not available.', status: 404 });
  });
});
