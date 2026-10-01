import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router';
import { expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { SettingsPage } from '@/features/settings/SettingsPage';

it.each(['PMAdmin', 'PMStaff'])(
  'allows only an admin to configure staff portal management (%s)',
  async (role) => {
    let saved: unknown;
    const settings = {
      accountingBasis: 'cash',
      moneyNegativeDisplay: 'minus',
      legalName: 'Example PM',
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
      lateFeeRateBps: 0,
      staffCanManagePortalAccess: true,
    };
    server.use(
      http.get('/api/auth/me', () =>
        HttpResponse.json({
          role,
          userId: 'manager',
          mfaEnabled: false,
          mfaEnrollmentRequired: false,
        }),
      ),
      http.get('/api/settings/org', () => HttpResponse.json(settings)),
      http.get('/api/settings/banks', () => HttpResponse.json([])),
      http.put('/api/settings/portal-access', async ({ request }) => {
        saved = await request.json();
        return HttpResponse.json({ ...settings, staffCanManagePortalAccess: false });
      }),
    );
    render(
      <QueryClientProvider
        client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
      >
        <MemoryRouter>
          <SettingsPage />
        </MemoryRouter>
      </QueryClientProvider>,
    );
    const input = await screen.findByRole('checkbox', {
      name: 'Allow staff to manage portal invitations and access',
    });
    if (role === 'PMStaff') {
      expect(input).toBeDisabled();
      expect(
        screen.queryByRole('button', { name: 'Save portal permissions' }),
      ).not.toBeInTheDocument();
    } else {
      await userEvent.click(input);
      await userEvent.click(screen.getByRole('button', { name: 'Save portal permissions' }));
      expect(await screen.findByText('Portal permissions saved.')).toBeInTheDocument();
      expect(saved).toEqual({ staffCanManagePortalAccess: false });
    }
  },
);
