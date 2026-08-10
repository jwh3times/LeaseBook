import type { CreateClientConfig } from './generated/client.gen';

// Same-origin requests are proxied to the host in development and served by it in production. Keep
// credentials on every call so the authentication and antiforgery cookies follow the request. Fetch
// is resolved lazily so MSW and other test harnesses can replace globalThis.fetch after module load.
export const createClientConfig: CreateClientConfig = (config) => ({
  ...config,
  baseUrl: window.location.origin,
  credentials: 'include',
  fetch: (request) => globalThis.fetch(request),
});
