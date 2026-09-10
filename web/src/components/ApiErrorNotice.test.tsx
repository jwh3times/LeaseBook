import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ApiErrorNotice } from './ApiErrorNotice';

describe('ApiErrorNotice', () => {
  it('renders nothing when there is no error', () => {
    render(<ApiErrorNotice error={null} />);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('renders the message and the selectable reference when present', () => {
    render(
      <ApiErrorNotice error={{ message: 'That entry was not found.', correlationId: 'abc123' }} />,
    );
    expect(screen.getByRole('alert')).toHaveTextContent('That entry was not found.');
    expect(screen.getByText('Reference: abc123')).toBeInTheDocument();
  });

  it('omits the reference when absent', () => {
    render(<ApiErrorNotice error={{ message: 'Nope.' }} />);
    expect(screen.queryByText(/Reference:/)).not.toBeInTheDocument();
  });

  it('renders the distinct internal_error copy instead of the server message', () => {
    render(
      <ApiErrorNotice
        error={{ code: 'internal_error', message: 'raw server text', correlationId: 'c' }}
      />,
    );
    expect(screen.getByRole('alert')).toHaveTextContent(/something went wrong on our end/i);
    expect(screen.queryByText('raw server text')).not.toBeInTheDocument();
  });

  // `UnhandledExceptionHandler` stamps `internal_error` on every unhandled exception, so this is
  // the copy a real production 500 produces — on a read as much as on a write.
  it('does not tell a read that nothing was saved, because a read was never saving', () => {
    render(<ApiErrorNotice error={{ code: 'internal_error', message: 'x' }} kind="read" />);
    expect(screen.getByRole('alert')).toHaveTextContent('Something went wrong on our end.');
    expect(screen.queryByText(/nothing was saved/i)).not.toBeInTheDocument();
  });

  it('still reassures a failed write that nothing was saved', () => {
    render(<ApiErrorNotice error={{ code: 'internal_error', message: 'x' }} />);
    expect(screen.getByRole('alert')).toHaveTextContent(/nothing was saved/i);
  });

  it('keeps the reference on the internal_error path, which is the only clue left', () => {
    render(
      <ApiErrorNotice error={{ code: 'internal_error', message: 'x', correlationId: 'ref9' }} />,
    );
    expect(screen.getByText('Reference: ref9')).toBeInTheDocument();
  });
  // #357: an expired session used to render the surface's own fallback ("Failed to load the
  // register.") because a body-less 401 carries no detail — telling the operator the read failed
  // when the truth is that they are signed out.
  it('says plainly that the user is signed out on a body-less 401', () => {
    render(
      <ApiErrorNotice
        error={{ message: 'Failed to load the register.', status: 401 }}
        kind="read"
      />,
    );
    expect(screen.getByRole('alert')).toHaveTextContent(/signed out/i);
    expect(screen.queryByText('Failed to load the register.')).not.toBeInTheDocument();
  });

  // A write rejected for an expired session really did save nothing, and the operator may have just
  // typed a payment — so this keeps the reassurance, exactly as the internal_error branch does.
  it('reassures a rejected write that nothing was saved', () => {
    render(<ApiErrorNotice error={{ message: 'Unable to record the payment.', status: 401 }} />);
    expect(screen.getByRole('alert')).toHaveTextContent(/signed out/i);
    expect(screen.getByRole('alert')).toHaveTextContent(/nothing was saved/i);
  });

  it('does not tell a read that nothing was saved when its session expired', () => {
    render(<ApiErrorNotice error={{ message: 'Failed to load it.', status: 401 }} kind="read" />);
    expect(screen.getByRole('alert')).toHaveTextContent(/signed out/i);
    expect(screen.queryByText(/nothing was saved/i)).not.toBeInTheDocument();
  });

  // Keeps the credential-rejection 401s (login, MFA, recovery code) on the server's own wording.
  it('leaves a rejected credential saying what the server said', () => {
    render(
      <ApiErrorNotice
        error={{ message: 'Invalid credentials.', status: 401, code: 'invalid_credentials' }}
      />,
    );
    expect(screen.getByRole('alert')).toHaveTextContent('Invalid credentials.');
    expect(screen.getByRole('alert')).not.toHaveTextContent(/signed out/i);
  });
});
