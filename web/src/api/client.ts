import { client } from './generated/client.gen';
import { getApiAuthCsrf } from './generated/sdk.gen';

const UNSAFE_METHODS = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

function readCookie(name: string): string | null {
  const match = document.cookie.match(new RegExp(`(?:^|; )${name}=([^;]*)`));
  return match?.[1] ? decodeURIComponent(match[1]) : null;
}

// Cookie-to-header XSRF (P12): echo the JS-readable XSRF-TOKEN cookie on unsafe requests as the
// X-XSRF-TOKEN header. The SPA refreshes the cookie via GET /api/auth/csrf when the auth state changes.
client.interceptors.request.use((request) => {
  if (UNSAFE_METHODS.has(request.method.toUpperCase())) {
    const token = readCookie('XSRF-TOKEN');
    if (token) {
      request.headers.set('X-XSRF-TOKEN', token);
    }
  }
  return request;
});

/** Refresh the XSRF cookie — call before authenticated mutations / after auth-state changes. */
export async function primeCsrf(): Promise<void> {
  await getApiAuthCsrf();
}
