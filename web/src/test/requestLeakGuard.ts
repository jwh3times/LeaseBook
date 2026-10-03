// Finds requests a test sent but that had not reached the mock server when the test ended.
//
// msw looks up the handlers for a request a few ticks after the request is sent. A test that ends
// inside that gap leaves the request to be resolved after `resetHandlers`, under the NEXT test's
// handlers, where it can consume a one-shot response or bump a counter and fail a test that did
// nothing wrong. msw announces a request only at that lookup (`request:start`), so the send has to be
// recorded separately and the two paired up. The suite's `afterEach` then holds the test open until
// everything it sent has been dispatched, and fails it if something never is.
//
// A request counts as settled once it is dispatched, not once it completes: a test may park a read on
// purpose (`delay('infinite')`) and that read is already bound to the right handler.

export interface RequestLeakGuard {
  /** A request left the code under test. */
  sent(method: string, url: string): void;
  /** The mock server picked the request up and resolved its handlers. */
  dispatched(method: string, url: string): void;
  /** What has been sent and not yet dispatched. */
  pending(): string[];
  /** Forgets what is pending, so a request that never arrives is charged to one test only. */
  reset(): void;
}

export function createRequestLeakGuard(): RequestLeakGuard {
  const undispatched = new Map<string, number>();
  const key = (method: string, url: string) => `${method.toUpperCase()} ${url}`;

  return {
    sent(method, url) {
      const k = key(method, url);
      undispatched.set(k, (undispatched.get(k) ?? 0) + 1);
    },
    dispatched(method, url) {
      const k = key(method, url);
      const count = undispatched.get(k);
      // No matching send: a request an earlier test leaked, arriving late. That test already failed
      // for it, so it must not be charged against the current one.
      if (count === undefined) return;
      if (count === 1) undispatched.delete(k);
      else undispatched.set(k, count - 1);
    },
    pending() {
      return [...undispatched].flatMap(([k, count]) => Array<string>(count).fill(k));
    },
    reset() {
      undispatched.clear();
    },
  };
}

/** The message a leaking test fails with. */
export function leakedRequestMessage(leaked: readonly string[]): string {
  return [
    'This test sent a request that never reached the mock server:',
    ...leaked.map((request) => `  • ${request}`),
    'It would be answered by a later test’s handlers. Wait for what the request produces before the',
    'test returns.',
  ].join('\n');
}
