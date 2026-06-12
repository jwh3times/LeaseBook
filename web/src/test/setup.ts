// Registers jest-dom matchers (e.g. toBeInTheDocument) with Vitest's expect, plus their types.
import '@testing-library/jest-dom/vitest';
import { afterAll, afterEach, beforeAll } from 'vitest';
import { server } from './mocks/server';

// Mock the API for the whole web suite; tests override handlers per scenario via server.use(...).
beforeAll(() => server.listen({ onUnhandledRequest: 'bypass' }));
afterEach(() => server.resetHandlers());
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
