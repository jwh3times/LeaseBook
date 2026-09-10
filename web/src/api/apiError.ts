interface ProblemBody {
  code?: string;
  title?: string;
  detail?: string;
  correlationId?: string;
  errors?: Record<string, string[]>;
}

export interface ApiError {
  code?: string;
  message: string;
  correlationId?: string;
  /**
   * The HTTP status the failure carried. Domain copy sometimes keys off status rather than a body
   * code — a 404 with an empty body still means "not found" — so the component layer needs it here
   * rather than reaching back into the response (ADR-025).
   */
  status?: number;
}

/**
 * The single API-error mapper (ADR-025). Replaces five copy-pasted variants that had already
 * drifted — reports.ts had silently dropped the validation branch.
 *
 * `fallbackMessage` is used only when the server body carries no `detail`, `title`, or validation
 * entry: an empty 500 renders the caller's own wording ("Failed to load the register") rather than
 * a bare status line, while a server that did explain itself always wins.
 */
export function toApiError(
  error: unknown,
  status: number,
  fallbackMessage = `Request failed (${status}).`,
): ApiError {
  const body = (error ?? {}) as ProblemBody;
  const firstValidation = body.errors ? Object.values(body.errors)[0]?.[0] : undefined;

  return {
    code: body.code ?? body.title,
    message: firstValidation ?? body.detail ?? body.title ?? fallbackMessage,
    correlationId: body.correlationId,
    status,
  };
}

/** Normalizes an unknown caught value (download helpers, fetch failures) into an ApiError. */
export function asApiError(e: unknown, fallback = 'Request failed.'): ApiError {
  if (e && typeof e === 'object' && 'message' in e) {
    const m = e as {
      message?: unknown;
      code?: unknown;
      correlationId?: unknown;
      status?: unknown;
    };
    return {
      message: typeof m.message === 'string' && m.message ? m.message : fallback,
      code: typeof m.code === 'string' ? m.code : undefined,
      correlationId: typeof m.correlationId === 'string' ? m.correlationId : undefined,
      status: typeof m.status === 'number' ? m.status : undefined,
    };
  }
  return { message: fallback, code: undefined, correlationId: undefined, status: undefined };
}

/**
 * Was this failure the server saying the record does not exist?
 *
 * A detail surface has two different failures to tell apart: the record is gone (a dead link, or it
 * was removed) and the read itself failed. Only the second is worth a support reference and a
 * retry — retrying a 404 just produces the same 404 — so the copy keys off status here rather than
 * treating every `isError` alike.
 */
export function isNotFound(error: unknown): boolean {
  return !!error && typeof error === 'object' && (error as { status?: unknown }).status === 404;
}

/**
 * Was this failure the server saying the caller is not signed in?
 *
 * An expired cookie produces a **body-less** 401: `OnRedirectToLogin` writes a bare status for
 * `/api` paths and there is no `UseStatusCodePages`, so there is no `detail` to render and no
 * `correlationId` to quote. `unwrap` then falls back to the surface's own copy — which is how a
 * signed-out operator came to be told "Failed to load the register" beside a Retry that re-issues
 * the same 401 forever (#357).
 *
 * Status alone is not the discriminator. `/api/auth/login`, `/api/auth/mfa` and the account-security
 * endpoints answer a *rejected credential* with 401 + ProblemDetails, so keying on 401 by itself
 * would tell someone who mistyped their password that their session had expired.
 *
 * Deliberately an allowlist of the two shapes that mean "signed out", rather than a denylist of the
 * credential-rejection codes: a 401 code added later then defaults to showing the server's own
 * message, which is merely unhelpful, instead of to a confident "you have been signed out", which
 * would be wrong and would hand the user a sign-in link they did not need. A 403 never qualifies —
 * an authorization failure must not sign anyone out.
 */
export function isSessionExpired(error: unknown): boolean {
  if (!error || typeof error !== 'object') return false;
  const { status, code } = error as { status?: unknown; code?: unknown };
  // `undefined` is the bare cookie-handler 401; `not_authenticated` is what the endpoints that read
  // the user themselves return when the cookie survived but the user behind it did not.
  return status === 401 && (code === undefined || code === 'not_authenticated');
}
