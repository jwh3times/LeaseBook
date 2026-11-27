import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router';
import { describe, expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { RunPreviewGrid, RunResultPanel } from './RunPreviewGrid';

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

describe('RunPreviewGrid — an excluded target', () => {
  it('words each reason the server sends instead of showing its code (#573)', () => {
    server.use(
      http.get('/api/operations/runs/rent/preview/issued-coverage', () =>
        HttpResponse.json({ rows: [] }),
      ),
    );
    render(
      <QueryClientProvider
        client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
      >
        <RunPreviewGrid
          rows={[
            row('a', 'Ada Tenant', { excludedReason: 'lease_not_in_schedule' }),
            row('b', 'Ben Tenant', { excludedReason: 'rent_zero' }),
          ]}
          exceptions={[]}
          selected={new Set()}
          selectable={false}
          issuedCoverage={{ type: 'rent', year: 2026, month: 5 }}
        />
      </QueryClientProvider>,
    );

    const ada = screen.getByText('Ada Tenant').closest('tr')!;
    expect(within(ada).getByText('Lease not active in this period')).toBeVisible();
    const ben = screen.getByText('Ben Tenant').closest('tr')!;
    expect(within(ben).getByText('Rent is zero')).toBeVisible();
    expect(screen.queryByText(/lease not in schedule|rent zero/i)).toBeNull();
  });
});

describe('RunResultPanel — after a run is confirmed', () => {
  it('links to what the run did to each target (#573)', () => {
    render(
      <QueryClientProvider
        client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
      >
        <MemoryRouter>
          <RunResultPanel
            runId="run-9"
            posted={0}
            skipped={2}
            excluded={1}
            total={0}
            onDone={() => {}}
          />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    expect(screen.getByRole('link', { name: 'View run' })).toHaveAttribute(
      'href',
      '/operations/runs/run-9',
    );
  });
});
