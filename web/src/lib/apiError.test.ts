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
});

describe('asApiError', () => {
  it('passes a thrown ApiError through and falls back for unknowns', () => {
    expect(asApiError({ message: 'Boom.', code: 'x', correlationId: 'c1' })).toEqual({
      message: 'Boom.',
      code: 'x',
      correlationId: 'c1',
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
