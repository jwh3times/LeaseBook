import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { UnmatchedObservations } from './UnmatchedObservations';

function show(count: number) {
  render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <UnmatchedObservations count={count} />
    </QueryClientProvider>,
  );
}

const row = {
  id: 'observation-1',
  receivedAt: '2026-10-04T12:00:00Z',
  kind: 'BankCredit',
  amount: 77.25,
  currency: 'USD',
  providerReference: 'sim_orphan',
  ageMinutes: 135,
};

describe('unmatched notifications', () => {
  it('shows nothing when there are none, and does not read the list', () => {
    let requested = false;
    server.use(
      http.get('/api/payments/unmatched', () => {
        requested = true;
        return HttpResponse.json({ items: [] });
      }),
    );
    show(0);
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
    expect(requested).toBe(false);
  });

  it('lists each notification with its five fields once opened, from the keyboard', async () => {
    server.use(http.get('/api/payments/unmatched', () => HttpResponse.json({ items: [row] })));
    show(1);
    expect(screen.getByRole('status')).toHaveTextContent('1 unmatched notifications');
    const toggle = screen.getByRole('button', { name: 'Show unmatched notifications' });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    toggle.focus();
    await userEvent.keyboard('{Enter}');

    const cells = within(await screen.findByRole('table')).getAllByRole('cell');
    expect(cells.map((cell) => cell.textContent)).toEqual([
      new Date(row.receivedAt).toLocaleString(),
      'BankCredit',
      '$77.25',
      'sim_orphan',
      '2 h 15 min',
    ]);
    expect(screen.getByRole('button', { name: 'Hide unmatched notifications' })).toHaveAttribute(
      'aria-expanded',
      'true',
    );
  });

  it('says so when the list is longer than what is shown', async () => {
    server.use(http.get('/api/payments/unmatched', () => HttpResponse.json({ items: [row] })));
    show(140);
    await userEvent.click(screen.getByRole('button', { name: 'Show unmatched notifications' }));
    expect(await screen.findByText('Showing the newest 1 of 140.')).toBeVisible();
  });

  it('shows the server reason and support reference, not an empty list, when the read fails', async () => {
    server.use(
      http.get('/api/payments/unmatched', () =>
        HttpResponse.json(
          {
            code: 'unavailable',
            detail: 'The notification inbox is unavailable.',
            correlationId: '1234567890abcdef1234567890abcdef',
          },
          { status: 503 },
        ),
      ),
    );
    show(2);
    await userEvent.click(screen.getByRole('button', { name: 'Show unmatched notifications' }));
    expect(await screen.findByText('The notification inbox is unavailable.')).toBeVisible();
    expect(screen.getByText('Reference: 1234567890abcdef1234567890abcdef')).toBeVisible();
    expect(screen.queryByText('No unmatched notifications.')).not.toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });
});
