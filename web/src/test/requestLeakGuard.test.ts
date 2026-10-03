import { describe, expect, it } from 'vitest';
import { createRequestLeakGuard, leakedRequestMessage } from './requestLeakGuard';

describe('request leak guard', () => {
  it('reports nothing when every request sent was dispatched', () => {
    const guard = createRequestLeakGuard();
    guard.sent('GET', 'http://localhost:3000/api/tenants');
    guard.dispatched('GET', 'http://localhost:3000/api/tenants');
    expect(guard.pending()).toEqual([]);
  });

  it('reports a request that was sent but never dispatched', () => {
    const guard = createRequestLeakGuard();
    guard.sent('post', 'http://localhost:3000/api/runs/confirm');
    expect(guard.pending()).toEqual(['POST http://localhost:3000/api/runs/confirm']);
  });

  it('counts repeated requests to one URL separately', () => {
    const guard = createRequestLeakGuard();
    guard.sent('GET', 'http://localhost:3000/api/preview');
    guard.sent('GET', 'http://localhost:3000/api/preview');
    guard.dispatched('GET', 'http://localhost:3000/api/preview');
    expect(guard.pending()).toEqual(['GET http://localhost:3000/api/preview']);
  });

  it('charges a leak to the test that sent it, not to the next one', () => {
    const guard = createRequestLeakGuard();
    guard.sent('GET', 'http://localhost:3000/api/preview');
    expect(guard.pending()).toHaveLength(1);
    guard.reset();

    // The leaked request is dispatched during the next test, which sent nothing itself.
    guard.dispatched('GET', 'http://localhost:3000/api/preview');
    expect(guard.pending()).toEqual([]);
  });

  it('names each leaked request in the failure message', () => {
    const message = leakedRequestMessage(['GET http://localhost:3000/api/preview']);
    expect(message).toContain('• GET http://localhost:3000/api/preview');
  });
});
