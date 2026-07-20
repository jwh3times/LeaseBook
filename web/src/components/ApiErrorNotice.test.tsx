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
});
