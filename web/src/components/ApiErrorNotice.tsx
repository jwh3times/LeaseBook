import type { CSSProperties } from 'react';
import { Icon } from '@/design';
import type { ApiError } from '@/lib/apiError';

export interface ApiErrorNoticeProps {
  error: ApiError | null;
  /** Shown when the mapper produced an empty message (parity with the old per-surface fallbacks). */
  fallback?: string;
  className?: string;
  style?: CSSProperties;
}

/**
 * The one mutation-error alert (ADR-025): the mapped message — or the distinct internal_error
 * copy — plus the selectable support reference when the server supplied one.
 */
export function ApiErrorNotice({
  error,
  fallback = 'Request failed.',
  className,
  style,
}: ApiErrorNoticeProps) {
  if (!error) return null;

  const message =
    error.code === 'internal_error'
      ? 'Something went wrong on our end. Nothing was saved.'
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
