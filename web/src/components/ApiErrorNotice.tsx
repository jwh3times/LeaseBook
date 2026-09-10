import type { CSSProperties } from 'react';
import { Icon } from '@/design';
import { isSessionExpired, type ApiError } from '@/api';

export interface ApiErrorNoticeProps {
  error: ApiError | null;
  /** Shown when the mapper produced an empty message (parity with the old per-surface fallbacks). */
  fallback?: string;
  /**
   * What failed. Only the `internal_error` copy differs: "Nothing was saved" is a true and
   * reassuring thing to say about a rejected write, and a false one about a read, which was never
   * saving anything. Defaults to `'write'` because every call site predating the read conversion
   * is a mutation.
   */
  kind?: 'read' | 'write';
  className?: string;
  style?: CSSProperties;
}

/**
 * The one API-error alert (ADR-025): the mapped message — or the distinct internal_error copy —
 * plus the selectable support reference when the server supplied one. Mutations only, until the
 * 2026-08-20 amendment put reads through the same success rule; a failed read now arrives here
 * carrying the same `code` and `correlationId`.
 *
 * A read passes `kind="read"` so the internal_error and session-expiry copy does not claim "Nothing was saved" about
 * an operation that was never saving. `UnhandledExceptionHandler` stamps `internal_error` on every
 * unhandled exception, so this is the copy a production 500 actually produces — not an edge case.
 */
export function ApiErrorNotice({
  error,
  fallback = 'Request failed.',
  kind = 'write',
  className,
  style,
}: ApiErrorNoticeProps) {
  if (!error) return null;

  // Session expiry is checked before `internal_error` and before the server's own message: an
  // expired cookie carries neither, so the only copy left to fall back on is the *caller's* — which
  // says the read failed, when the truth is that the user is signed out (#357).
  const message = isSessionExpired(error)
    ? kind === 'read'
      ? 'You have been signed out. Sign in again to continue.'
      : 'You have been signed out. Nothing was saved — sign in again and retry.'
    : error.code === 'internal_error'
      ? kind === 'read'
        ? 'Something went wrong on our end.'
        : 'Something went wrong on our end. Nothing was saved.'
      : error.message || fallback;

  return (
    <span className={`pf-api-error${className ? ` ${className}` : ''}`} role="alert" style={style}>
      <Icon name="alert" size={14} /> <span>{message}</span>
      {error.correlationId && (
        <code className="pf-error-ref t3">Reference: {error.correlationId}</code>
      )}
    </span>
  );
}
