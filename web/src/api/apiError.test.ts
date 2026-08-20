import { describe, expect, it } from 'vitest';
import { asApiError, toApiError } from './apiError';

describe('toApiError', () => {
  it('prefers the first validation message, then detail, then title', () => {
    expect(
      toApiError({ errors: { endDate: ['End date must be on or after the start date.'] } }, 400)
        .message,
    ).toBe('End date must be on or after the start date.');
    expect(toApiError({ detail: 'Detail wins over title.', title: 'code_x' }, 409).message).toBe(
      'Detail wins over title.',
    );
  });

  it('carries the code and correlation id through', () => {
    const result = toApiError(
      { code: 'internal_error', detail: 'x', correlationId: 'abc123' },
      500,
    );
    expect(result.code).toBe('internal_error');
    expect(result.correlationId).toBe('abc123');
  });

  it('falls back to a generic message when the body is empty', () => {
    expect(toApiError({}, 503).message).toBe('Request failed (503).');
  });

  it('prefers the caller fallback over the generic status line, and the body over both', () => {
    expect(toApiError({}, 503, 'Failed to load the register').message).toBe(
      'Failed to load the register',
    );
    expect(
      toApiError({ detail: 'The register is rebuilding.' }, 503, 'Failed to load the register')
        .message,
    ).toBe('The register is rebuilding.');
  });

  // Domain copy sometimes keys off status rather than a body code — a 404 with an empty body still
  // means "not found" — so the component layer needs the status on the error itself.
  it('records the status', () => {
    expect(toApiError({}, 404).status).toBe(404);
  });
});

describe('asApiError', () => {
  it('passes a thrown ApiError through and falls back for unknowns', () => {
    expect(asApiError({ message: 'Boom.', code: 'x', correlationId: 'c1', status: 409 })).toEqual({
      message: 'Boom.',
      code: 'x',
      correlationId: 'c1',
      status: 409,
    });
    expect(asApiError(new TypeError('fetch failed'), 'Download failed.').message).toBe(
      'fetch failed',
    );
    expect(asApiError(null, 'Download failed.')).toEqual({
      message: 'Download failed.',
      code: undefined,
      correlationId: undefined,
    });
  });
});
