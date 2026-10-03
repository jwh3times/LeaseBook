// Registers jest-dom matchers (e.g. toBeInTheDocument) with Vitest's expect, plus their types.
import '@testing-library/jest-dom/vitest';
import { afterAll, afterEach, beforeAll, expect, vi } from 'vitest';
import { server } from './mocks/server';
import { createRequestLeakGuard, leakedRequestMessage } from './requestLeakGuard';

const requestLeaks = createRequestLeakGuard();

// Mock the API for the whole web suite; tests override handlers per scenario via server.use(...).
beforeAll(() => {
  server.listen({ onUnhandledFrame: 'bypass' });

  // Record every send on top of the server's own patch, which is the only layer that sees a request
  // before its handlers are looked up. An unhandled request is bypassed but still announced, so it
  // counts as dispatched like any other.
  const send = globalThis.fetch;
  globalThis.fetch = (...args: Parameters<typeof fetch>) => {
    const [input, init] = args;
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    requestLeaks.sent(
      init?.method ?? (input instanceof Request ? input.method : 'GET'),
      new URL(url, window.location.href).href,
    );
    return Reflect.apply(send, globalThis, args);
  };
  server.events.on('request:start', ({ request }) =>
    requestLeaks.dispatched(request.method, request.url),
  );
});
// Hold each test open until everything it sent has been dispatched, so no request is answered by the
// next test's handlers; `resetHandlers` only runs once that is true or the wait gives up.
afterEach(async ({ task }) => {
  const settled = await vi
    .waitFor(() => expect(requestLeaks.pending()).toEqual([]), { timeout: 1000, interval: 1 })
    .then(
      () => true,
      () => false,
    );
  const leaked = requestLeaks.pending();
  requestLeaks.reset();
  server.resetHandlers();
  // A test that already failed stopped partway, so what it left in flight is a symptom, not a finding.
  if (!settled && task.result?.state !== 'fail') {
    throw new Error(leakedRequestMessage(leaked));
  }
});
afterAll(() => server.close());

// This jsdom build does not provide localStorage; give tests an in-memory Storage so code that
// persists preferences (e.g. ThemeProvider) is exercised rather than silently no-op'd.
if (typeof globalThis.localStorage === 'undefined') {
  const store = new Map<string, string>();
  const storage: Storage = {
    get length() {
      return store.size;
    },
    clear: () => store.clear(),
    getItem: (key) => store.get(key) ?? null,
    key: (index) => [...store.keys()][index] ?? null,
    removeItem: (key) => {
      store.delete(key);
    },
    setItem: (key, value) => {
      store.set(key, String(value));
    },
  };
  Object.defineProperty(globalThis, 'localStorage', { value: storage, configurable: true });
  Object.defineProperty(window, 'localStorage', { value: storage, configurable: true });
}

// jsdom implements no layout, so it ships no `Element.scrollIntoView`. Calling it throws rather than
// no-op'ing, which surfaces as the whole component failing to render. Real browsers always have it,
// so this belongs here rather than as an optional call in app code that would weaken the contract.
if (typeof Element.prototype.scrollIntoView !== 'function') {
  Element.prototype.scrollIntoView = () => {};
}
