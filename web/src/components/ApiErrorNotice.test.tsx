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
});
