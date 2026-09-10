import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { ErrorAction } from './ErrorAction';

describe('ErrorAction', () => {
  it('offers a retry for an ordinary read failure', async () => {
    const onRetry = vi.fn();
    render(<ErrorAction error={{ message: 'Boom.', status: 500 }} onRetry={onRetry} />);

    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(onRetry).toHaveBeenCalledOnce();
  });

  it('renders nothing when there is no retry and no session problem', () => {
    const { container } = render(<ErrorAction error={{ message: 'Boom.', status: 500 }} />);
    expect(container).toBeEmptyDOMElement();
  });

  // #357: a retry re-issues the same request, gets the same 401, and never resolves.
  it('replaces the retry with a sign-in link when the session has expired', () => {
    const onRetry = vi.fn();
    render(<ErrorAction error={{ message: 'x', status: 401 }} onRetry={onRetry} />);

    expect(screen.queryByRole('button', { name: /retry/i })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Sign in' })).toHaveAttribute('href', '/login');
  });

  // The surface may have had nothing to retry, but it always has somewhere to send a signed-out user.
  it('offers sign-in even where there was no retry to replace', () => {
    render(<ErrorAction error={{ message: 'x', status: 401 }} />);
    expect(screen.getByRole('link', { name: 'Sign in' })).toBeInTheDocument();
  });

  it('keeps a rejected credential on the retry, since it is not an expired session', () => {
    render(
      <ErrorAction
        error={{ message: 'x', status: 401, code: 'invalid_credentials' }}
        onRetry={vi.fn()}
      />,
    );
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /sign in/i })).not.toBeInTheDocument();
  });

  // Both branches must carry the same class, or the dashboard's banner link restyles on expiry.
  it('applies an overriding className to whichever control it renders', () => {
    const { rerender } = render(
      <ErrorAction
        error={{ status: 500 }}
        onRetry={vi.fn()}
        className="ob-migration-banner-link"
      />,
    );
    expect(screen.getByRole('button')).toHaveClass('ob-migration-banner-link');

    rerender(<ErrorAction error={{ status: 401 }} className="ob-migration-banner-link" />);
    expect(screen.getByRole('link')).toHaveClass('ob-migration-banner-link');
  });

  it('wears the design system button classes by default rather than a copied literal', () => {
    render(<ErrorAction error={{ status: 401 }} />);
    expect(screen.getByRole('link')).toHaveClass('pf-btn', 'v-ghost', 's-sm');
  });
});
