import { isSessionExpired } from '@/api';
import { buttonClassName } from '@/design';

export interface ErrorActionProps {
  /** The failure being reported — the same value handed to the sibling `ApiErrorNotice`. */
  error: unknown;
  /** Retries the failed read. Omit when the surface has nothing useful to retry. */
  onRetry?: () => void;
  /** Disables the retry and swaps its label while a refetch is in flight. */
  retrying?: boolean;
  /**
   * Replaces the default ghost-button classes. For the one surface whose affordance is styled as
   * something else — the dashboard's migration banner link — so that it keeps its own look while
   * still getting the right affordance.
   */
  className?: string;
}

/**
 * The affordance that belongs beside a reported failure — the one place that decides *which* one.
 *
 * A retry is the right answer to almost every read error and the wrong answer to exactly one: an
 * expired session re-issues the same request, gets the same 401, and never resolves (#357). Eight
 * surfaces had each hand-rolled the same ghost Retry button next to an `ApiErrorNotice`, so fixing
 * that in `QueryErrorState` alone left seven of them saying "You have been signed out" beside a
 * button that could not act on it — copy contradicting affordance. One component now owns the
 * decision, for the same reason `ProblemResults` and `unwrap` exist (ADR-025).
 *
 * The sign-in path is a real anchor, not a router navigation: the session is dead, so a full
 * document load drops every cached query and stale row with it — the reasoning behind the hard
 * `window.location.assign('/login')` after an explicit sign-out in `AccountSecurityPage`.
 *
 * Renders a plain `<button>` rather than `Button` so both branches carry the identical class, which
 * is what lets the anchor wear the button shape without copying its scheme. The retry never takes
 * an icon, so nothing of `Button` is lost.
 */
export function ErrorAction({ error, onRetry, retrying, className }: ErrorActionProps) {
  const classes = className ?? buttonClassName('ghost', 'sm');

  if (isSessionExpired(error)) {
    return (
      <a className={classes} href="/login">
        Sign in
      </a>
    );
  }
  if (!onRetry) return null;
  return (
    <button type="button" className={classes} onClick={onRetry} disabled={retrying}>
      {retrying ? 'Retrying…' : 'Retry'}
    </button>
  );
}
