import type { UseQueryResult } from '@tanstack/react-query';
import { Button, EmptyState } from '@/design';
import { ApiErrorNotice } from './ApiErrorNotice';

export interface QueryErrorStateProps {
  /** The failed query. Only `error`, `refetch` and `isFetching` are read. */
  query: Pick<UseQueryResult<unknown>, 'error' | 'refetch' | 'isFetching'>;
  /** What could not be loaded, as a heading — the wording the surface used before it could retry. */
  title: string;
  /** User-visible copy for the case where the server body carried no `detail`, `title` or code. */
  fallback: string;
  /**
   * Replaces the default `query.refetch()`. A surface holding state derived from the rows — a bulk
   * run's tick selection, say — resets that here, because a refetch returns a different row set and
   * the selection is no longer about the rows on screen.
   */
  onRetry?: () => void;
}

/**
 * The read-error state for a **primary-content** region (ADR-025, 2026-09-09 amendment).
 *
 * These regions used to render a failed read as an `EmptyState` with a handwritten description,
 * which threw away the `code` and `correlationId` that `unwrap` carries — leaving the operator with
 * no `Reference: <32-hex>` to quote and no way to retry without a full page reload. The layout is
 * deliberately still `EmptyState`'s, so the only thing that changes on these surfaces is that the
 * description is now the server's own mapped message plus the support reference, and there is a
 * retry.
 *
 * Auxiliary reads — a selector, a label, a count — stay inline; see the `col gap6` pattern in
 * `.claude/agents/react-frontend.md`. This is the block-level sibling of that, not a replacement.
 */
export function QueryErrorState({ query, title, fallback, onRetry }: QueryErrorStateProps) {
  return (
    <EmptyState
      icon="alert"
      title={title}
      description={
        <ApiErrorNotice
          error={query.error}
          fallback={fallback}
          kind="read"
          className="pf-api-error-block"
        />
      }
      action={
        <Button
          variant="ghost"
          size="sm"
          onClick={() => (onRetry ? onRetry() : void query.refetch())}
          disabled={query.isFetching}
        >
          {query.isFetching ? 'Retrying…' : 'Retry'}
        </Button>
      }
    />
  );
}
