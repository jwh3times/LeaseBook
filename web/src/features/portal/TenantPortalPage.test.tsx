import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { http, HttpResponse, delay } from 'msw';
import { MemoryRouter } from 'react-router';
import { describe, expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { TenantPortalPage } from './TenantPortalPage';

function show() {
  render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter>
        <TenantPortalPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('tenant rent ledger', () => {
  it('shows loading before any balance is asserted', async () => {
    server.use(
      http.get('/api/portal/tenant/ledger', async () => {
        await delay('infinite');
        return HttpResponse.json({});
      }),
    );
    show();
    expect(await screen.findByRole('status')).toHaveTextContent('Loading rent ledger');
    expect(screen.queryByText('Rent ledger balance')).not.toBeInTheDocument();
  });
  it('renders an empty ledger honestly', async () => {
    server.use(
      http.get('/api/portal/tenant/ledger', () =>
        HttpResponse.json({ residentName: 'Resident A', balance: 0, rows: [] }),
      ),
    );
    show();
    expect(await screen.findByText('No rent ledger activity yet')).toBeInTheDocument();
    expect(screen.getByText('Rent ledger balance')).toBeInTheDocument();
  });
  it('renders money and explicit void/reversal labels', async () => {
    server.use(
      http.get('/api/portal/tenant/ledger', () =>
        HttpResponse.json({
          residentName: 'Resident A',
          balance: 0,
          rows: [
            {
              date: '2026-09-01',
              category: 'Fee',
              charge: 25,
              payment: 0,
              balance: 25,
              isVoided: true,
              isReversal: false,
            },
            {
              date: '2026-09-02',
              category: 'Reversal',
              charge: 0,
              payment: 25,
              balance: 0,
              isVoided: false,
              isReversal: true,
            },
          ],
        }),
      ),
    );
    show();
    expect(await screen.findByText('Voided')).toBeInTheDocument();
    expect(screen.getAllByText('Reversal')).toHaveLength(2);
    expect(screen.getAllByText('$25.00')[0]).toHaveClass('pf-money');
    expect(screen.getByRole('link', { name: 'Account security' })).toHaveAttribute(
      'href',
      '/account/security',
    );
  });
  it('keeps failed reads distinct and preserves the support reference', async () => {
    const reference = '1234567890abcdef1234567890abcdef';
    server.use(
      http.get('/api/portal/tenant/ledger', () =>
        HttpResponse.json(
          {
            code: 'read_failed',
            detail: 'Ledger temporarily unavailable.',
            correlationId: reference,
          },
          { status: 500 },
        ),
      ),
    );
    show();
    expect(await screen.findByText('Ledger temporarily unavailable.')).toBeInTheDocument();
    expect(screen.getByText(`Reference: ${reference}`)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /retry/i })).toBeInTheDocument();
    expect(screen.queryByText('Rent ledger balance')).not.toBeInTheDocument();
  });
  it('names denied resident access', async () => {
    server.use(
      http.get('/api/portal/tenant/ledger', () => new HttpResponse(null, { status: 403 })),
    );
    show();
    expect(await screen.findByText('Resident access unavailable')).toBeInTheDocument();
    expect(screen.queryByText('No rent ledger activity yet')).not.toBeInTheDocument();
  });
  it('offers sign-in for an expired session', async () => {
    server.use(
      http.get('/api/portal/tenant/ledger', () =>
        HttpResponse.json({ code: 'not_authenticated' }, { status: 401 }),
      ),
    );
    show();
    expect(await screen.findByRole('link', { name: /sign in/i })).toHaveAttribute('href', '/login');
    expect(screen.queryByRole('button', { name: /retry/i })).not.toBeInTheDocument();
  });
});
