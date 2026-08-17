import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import type { TenantDetail } from '@/lib/directory';
import { LeaseLateFeeModal } from './LeaseLateFeeModal';

const ORG = {
  accountingBasis: 'cash',
  moneyNegativeDisplay: 'minus',
  legalName: 'Tarheel Property Group',
  address: null,
  city: null,
  state: null,
  zip: null,
  phone: null,
  logoBlobRef: null,
  rentDueDay: 1,
  lateFeeGraceDays: 5,
  lateFeeKind: 'flat',
  lateFeeAmount: 50,
  lateFeeRateBps: 500,
};

function detailWith(overrides: Partial<NonNullable<TenantDetail['lease']>>): TenantDetail {
  return {
    id: 't1',
    displayName: 'Jasmine Carter',
    contact: { email: null, phone: null },
    lifecycleStatus: 'current',
    financialStanding: { delinquentBalance: 0, unappliedCredit: 0 },
    lease: {
      id: 'l1',
      unitId: 'u1',
      startDate: '2025-06-01',
      endDate: '2026-05-31',
      rent: 1450,
      depositRequired: 1450,
      status: 'active',
      lateFeeRentDueDayOverride: null,
      lateFeeGraceDaysOverride: null,
      lateFeeKindOverride: null,
      lateFeeAmountOverride: null,
      lateFeeRateBpsOverride: null,
      ...overrides,
    },
    unitLabel: '#2B',
    propertyAddress: '412 Oakmont Ave',
    ownerId: 'o1',
    ownerName: 'Hargrove',
    balance: 0,
    depositHeld: 0,
  };
}

function renderModal(detail: TenantDetail) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <LeaseLateFeeModal tenantId="t1" detail={detail} onClose={() => {}} />
    </QueryClientProvider>,
  );
}

describe('LeaseLateFeeModal', () => {
  it('shows inherited fields with the org default and sends null for them', async () => {
    let saved: Record<string, unknown> | null = null;
    server.use(
      http.get('/api/settings/org', () => HttpResponse.json(ORG)),
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.put('/api/directory/leases/:id', async ({ request }) => {
        saved = (await request.json()) as Record<string, unknown>;
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderModal(detailWith({}));

    // Every field inherits, and the org value is visible without leaving the dialog.
    expect(await screen.findByRole('option', { name: 'Inherit (5 days)' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'Inherit ($50.00)' })).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: /save overrides/i }));

    // Inherit must serialize as null, not as the org value copied onto the lease — copying would
    // silently pin the lease to today's default and break inheritance for every future change.
    expect(saved).toMatchObject({
      lateFeeRentDueDayOverride: null,
      lateFeeGraceDaysOverride: null,
      lateFeeKindOverride: null,
      lateFeeAmountOverride: null,
      lateFeeRateBpsOverride: null,
    });
  });

  it('keeps an explicit zero override distinct from inherit', async () => {
    let saved: Record<string, unknown> | null = null;
    server.use(
      http.get('/api/settings/org', () => HttpResponse.json(ORG)),
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.put('/api/directory/leases/:id', async ({ request }) => {
        saved = (await request.json()) as Record<string, unknown>;
        return new HttpResponse(null, { status: 204 });
      }),
    );

    // Grace days explicitly overridden to 0 — "no grace period", which is not the same as
    // inheriting the org's 5 and must survive a round trip as 0 rather than collapsing to null.
    renderModal(detailWith({ lateFeeGraceDaysOverride: 0 }));

    expect(await screen.findByDisplayValue('0')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: /save overrides/i }));

    expect(saved).toMatchObject({ lateFeeGraceDaysOverride: 0 });
  });

  it('round-trips a rate override as basis points while showing a percentage', async () => {
    let saved: Record<string, unknown> | null = null;
    server.use(
      http.get('/api/settings/org', () => HttpResponse.json(ORG)),
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.put('/api/directory/leases/:id', async ({ request }) => {
        saved = (await request.json()) as Record<string, unknown>;
        return new HttpResponse(null, { status: 204 });
      }),
    );

    // 750 bps must read as 7.5%, not 750.
    renderModal(detailWith({ lateFeeKindOverride: 'percent', lateFeeRateBpsOverride: 750 }));

    expect(await screen.findByDisplayValue('7.5')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: /save overrides/i }));

    expect(saved).toMatchObject({ lateFeeKindOverride: 'percent', lateFeeRateBpsOverride: 750 });
  });

  it('sends the untouched lease fields back unchanged', async () => {
    let saved: Record<string, unknown> | null = null;
    server.use(
      http.get('/api/settings/org', () => HttpResponse.json(ORG)),
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.put('/api/directory/leases/:id', async ({ request }) => {
        saved = (await request.json()) as Record<string, unknown>;
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderModal(detailWith({}));
    await userEvent.click(await screen.findByRole('button', { name: /save overrides/i }));

    // UpdateLease replaces the lease wholesale, so omitting rent/dates/status here would blank or
    // reset them as a side effect of editing a late fee.
    expect(saved).toMatchObject({
      tenantId: 't1',
      unitId: 'u1',
      startDate: '2025-06-01',
      endDate: '2026-05-31',
      rent: 1450,
      depositRequired: 1450,
      status: 'active',
    });
  });
});
