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
