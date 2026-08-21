import { client } from './generated/client.gen';
import { getApiAuthCsrf } from './generated/sdk.gen';

const UNSAFE_METHODS = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);
const CSRF_COOKIE = 'XSRF-TOKEN';
const CSRF_HEADER = 'X-XSRF-TOKEN';

function readCookie(name: string): string | null {
  const match = document.cookie.match(new RegExp(`(?:^|; )${name}=([^;]*)`));
  return match?.[1] ? decodeURIComponent(match[1]) : null;
}

// One prime at a time: concurrent mutations on an unprimed session share the refresh instead of
// racing GET /api/auth/csrf.
let priming: Promise<void> | null = null;

/**
 * Refresh the XSRF cookie. Auth-state changes (login page mount, sign-in, sign-out) call this so
 * the next mutation does not pay a rejection round-trip; ordinary mutations must NOT — the
 * interceptors below prime on demand and replay once on a stale token, so "remember to prime" is
 * no longer a call-site precondition.
 */
export function primeCsrf(): Promise<void> {
  priming ??= getApiAuthCsrf().then(
    () => {
      priming = null;
    },
    (cause: unknown) => {
      priming = null;
      throw cause;
    },
  );
  return priming;
}

// A pristine clone of each unsafe request, taken before fetch consumes the body, so the response
// interceptor can replay it once if the server rejects the token as stale.
const replayable = new WeakMap<Request, Request>();

// Cookie-to-header XSRF (P12): echo the JS-readable XSRF-TOKEN cookie on unsafe requests as the
// X-XSRF-TOKEN header. An unprimed session (first-ever mutation, or the session cookie evicted
// while the 8-hour auth cookie lives on) fetches a token first rather than failing. Priming is
// best-effort here — the server's verdict is the real gate, and the replay below handles a miss.
client.interceptors.request.use(async (request) => {
  if (!UNSAFE_METHODS.has(request.method.toUpperCase())) {
    return request;
  }
  if (!readCookie(CSRF_COOKIE)) {
    await primeCsrf().catch(() => undefined);
  }
  const token = readCookie(CSRF_COOKIE);
  if (token) {
    request.headers.set(CSRF_HEADER, token);
  }
  replayable.set(request, request.clone());
  return request;
});

async function isAntiforgeryRejection(response: Response): Promise<boolean> {
  try {
    const body = (await response.clone().json()) as { code?: string; title?: string };
    return (body.code ?? body.title) === 'antiforgery_rejected';
  } catch {
    return false;
  }
}

// The antiforgery request token is user-bound, so a present cookie can still be stale — minted for
// a different signed-in state (login/logout, possibly in another tab). Cookie presence cannot
// detect that; the server's stable `antiforgery_rejected` code (ApiAntiforgeryMiddleware) can.
// Refresh the token and replay the request once; a second rejection surfaces as the error it is.
client.interceptors.response.use(async (response, request, options) => {
  if (response.status !== 400) {
    return response;
  }
  const replay = replayable.get(request);
  if (!replay || !(await isAntiforgeryRejection(response))) {
    return response;
  }
  replayable.delete(request);
  await primeCsrf().catch(() => undefined);
  const token = readCookie(CSRF_COOKIE);
  if (!token) {
    return response;
  }
  replay.headers.set(CSRF_HEADER, token);
  // The replayed response skips the interceptor chain, so it cannot replay again.
  return (options.fetch ?? globalThis.fetch)(replay);
});
