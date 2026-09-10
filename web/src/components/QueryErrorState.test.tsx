import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { QueryErrorState } from './QueryErrorState';

function query(error: unknown, isFetching = false) {
  return { error, isFetching, refetch: vi.fn() } as unknown as Parameters<
    typeof QueryErrorState
  >[0]['query'];
}

describe('QueryErrorState', () => {
  it('offers a retry for an ordinary read failure', async () => {
    const q = query({ message: 'Failed to load the register.', status: 500 });
    render(<QueryErrorState query={q} title="Couldn’t load the register" fallback="x" />);

    const retry = screen.getByRole('button', { name: 'Retry' });
    await userEvent.click(retry);
    expect(q.refetch).toHaveBeenCalledOnce();
  });

  // #357: the whole point. Retry re-issues the same read, gets the same 401, and never resolves —
  // so the affordance has to change, not just the copy.
  it('replaces Retry with a sign-in link when the session has expired', () => {
    render(
      <QueryErrorState
        query={query({ message: 'Failed to load the register.', status: 401 })}
        title="Couldn’t load the register"
        fallback="x"
      />,
    );

    expect(screen.queryByRole('button', { name: /retry/i })).not.toBeInTheDocument();
    const signIn = screen.getByRole('link', { name: 'Sign in' });
    // A real navigation, not a client-side route change: the session is dead, so every cached
    // query, in-memory value and stale row should go with it (cf. AccountSecurityPage's sign-out).
    expect(signIn).toHaveAttribute('href', '/login');
  });

  it('still offers Retry when a 401 was a rejected credential rather than an expired session', () => {
    render(
      <QueryErrorState
        query={query({ message: 'Invalid credentials.', status: 401, code: 'invalid_credentials' })}
        title="Couldn’t load it"
        fallback="x"
      />,
    );
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /sign in/i })).not.toBeInTheDocument();
  });

  it('does not disable the sign-in link while a doomed refetch is in flight', () => {
    render(
      <QueryErrorState
        query={query({ message: 'x', status: 401 }, true)}
        title="Couldn’t load it"
        fallback="x"
      />,
    );
    expect(screen.getByRole('link', { name: 'Sign in' })).toBeInTheDocument();
  });
});
