import createClient, { type Middleware } from 'openapi-fetch';
import type { paths } from './schema';

const UNSAFE_METHODS = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

function readCookie(name: string): string | null {
  const match = document.cookie.match(new RegExp(`(?:^|; )${name}=([^;]*)`));
  return match?.[1] ? decodeURIComponent(match[1]) : null;
}

// Cookie-to-header XSRF (P12): echo the JS-readable XSRF-TOKEN cookie on unsafe requests as the
// X-XSRF-TOKEN header. The SPA refreshes the cookie via GET /api/auth/csrf when the auth state changes.
const xsrfMiddleware: Middleware = {
  onRequest({ request }) {
    if (UNSAFE_METHODS.has(request.method.toUpperCase())) {
      const token = readCookie('XSRF-TOKEN');
      if (token) {
        request.headers.set('X-XSRF-TOKEN', token);
      }
    }
    return request;
  },
};

// Same-origin: requests hit /api, proxied to the host in dev (P16/P20) and served by it in prod.
// baseUrl is the current origin (absolute) — relative URLs are rejected by the fetch implementation
// the test runner uses, and the origin keeps us same-origin in the browser regardless. The fetch is
// resolved lazily per call so test mocks that patch globalThis.fetch after import are honored.
export const api = createClient<paths>({
  baseUrl: window.location.origin,
  credentials: 'include',
  fetch: (request) => globalThis.fetch(request),
});
api.use(xsrfMiddleware);

/** Refresh the XSRF cookie — call before authenticated mutations / after auth-state changes. */
export async function primeCsrf(): Promise<void> {
  await api.GET('/api/auth/csrf');
}
