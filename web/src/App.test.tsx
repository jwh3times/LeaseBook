import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { App } from '@/App';

describe('App', () => {
  it('redirects an unauthenticated visitor to the sign-in screen', async () => {
    // Default MSW handler: GET /api/auth/me → 401, so the guard sends us to /login.
    render(<App />);
    expect(await screen.findByRole('button', { name: /sign in/i })).toBeInTheDocument();
  });
});
