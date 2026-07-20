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
}

/**
 * The single API-error mapper (ADR-025). Replaces five copy-pasted variants that had already
 * drifted — reports.ts had silently dropped the validation branch.
 */
export function toApiError(error: unknown, status: number): ApiError {
  const body = (error ?? {}) as ProblemBody;
  const firstValidation = body.errors ? Object.values(body.errors)[0]?.[0] : undefined;

  return {
    code: body.code ?? body.title,
    message: firstValidation ?? body.detail ?? body.title ?? `Request failed (${status}).`,
    correlationId: body.correlationId,
  };
}

/** Normalizes an unknown caught value (download helpers, fetch failures) into an ApiError. */
export function asApiError(e: unknown, fallback = 'Request failed.'): ApiError {
  if (e && typeof e === 'object' && 'message' in e) {
    const m = e as { message?: unknown; code?: unknown; correlationId?: unknown };
    return {
      message: typeof m.message === 'string' && m.message ? m.message : fallback,
      code: typeof m.code === 'string' ? m.code : undefined,
      correlationId: typeof m.correlationId === 'string' ? m.correlationId : undefined,
    };
  }
  return { message: fallback, code: undefined, correlationId: undefined };
}
