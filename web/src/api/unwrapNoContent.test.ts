import { describe, expect, it } from 'vitest';
import { unwrap } from './request';

describe('unwrapNoContent', () => {
  it('accepts a successful empty mutation', async () => {
    await expect(
      unwrap(Promise.resolve({ response: new Response(null, { status: 204 }) }), 'Failed', {
        allowNoContent: true,
      }),
    ).resolves.toBeUndefined();
  });
  it('preserves the problem details from a failed mutation', async () => {
    await expect(
      unwrap(
        Promise.resolve({
          error: {
            code: 'invalid_credentials',
            detail: 'Current password is invalid.',
            correlationId: 'reference',
          },
          response: new Response(null, { status: 400 }),
        }),
        'Failed',
        { allowNoContent: true },
      ),
    ).rejects.toMatchObject({
      code: 'invalid_credentials',
      message: 'Current password is invalid.',
      correlationId: 'reference',
      status: 400,
    });
  });
});
