import type { DefaultOptions } from '@tanstack/react-query';

/**
 * The app's query defaults, shared by `Providers` and by tests that need the real stale time (#712):
 * a read is fresh for 30 seconds and is not refetched on window focus. A test that copied these by
 * hand would keep passing after the app's values changed.
 */
export const queryDefaults = {
  queries: { retry: 1, staleTime: 30_000, refetchOnWindowFocus: false },
} satisfies DefaultOptions;
