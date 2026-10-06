import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { RunPreviewGrid } from './RunPreviewGrid';

const row = (targetId: string, label: string, extra: Record<string, unknown> = {}) => ({
  targetId,
  targetKind: 'Lease',
  label,
  amount: 50,
  alreadyDone: false,
  excludedReason: null,
  detail: {},
  ...extra,
});

function show(selected: string[]) {
  server.use(
    http.get('/api/operations/runs/latefee/preview/issued-coverage', () =>
      HttpResponse.json({ rows: [] }),
    ),
  );
  render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <RunPreviewGrid
        rows={[
          row('a', 'Ada Tenant'),
          row('b', 'Ben Tenant', {
            caution: 'payment_in_transit',
            detail: { inTransit: '650.00', paidOn: '2026-05-02' },
          }),
        ]}
        exceptions={[]}
        selected={new Set(selected)}
        selectable
        onToggle={() => {}}
        onToggleAll={() => {}}
        issuedCoverage={{ type: 'latefee', year: 2026, month: 5 }}
      />
    </QueryClientProvider>,
  );
}

describe('RunPreviewGrid — a tenant with a payment in transit', () => {
  it('says so in words, with what was paid and when, and keeps the row selectable at the same fee', () => {
    show([]);
    const cautioned = screen.getByRole('checkbox', { name: 'Select Ben Tenant' }).closest('tr')!;
    expect(within(cautioned).getByText('Payment in transit')).toBeVisible();
    expect(cautioned).toHaveTextContent(
      'Paid $650.00 on 2026-05-02; not yet at the bank. Not included in "select all".',
    );
    expect(cautioned).toHaveTextContent('$50.00');
    expect(
      within(cautioned).getByRole('checkbox', { name: 'Select Ben Tenant' }),
    ).not.toBeChecked();
    // The other row reads as plainly eligible.
    const plain = screen.getByRole('checkbox', { name: 'Select Ada Tenant' }).closest('tr')!;
    expect(within(plain).getByText('Eligible')).toBeVisible();
  });

  it('reads select-all as complete once every uncautioned row is ticked', () => {
    show(['a']);
    expect(screen.getByRole('checkbox', { name: 'Select all eligible' })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Select Ben Tenant' })).not.toBeChecked();
  });
});
