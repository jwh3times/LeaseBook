import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import { getApiDashboard, getApiReportsByIdCsv } from './generated';
import { download, unwrap } from './request';
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
