import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import { CheckPrintSettingsDialog } from './CheckPrintSettingsDialog';

function renderDialog() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  renderWith(queryClient);
  return queryClient;
}

function renderWith(queryClient: QueryClient) {
  render(
    <QueryClientProvider client={queryClient}>
      <CheckPrintSettingsDialog
        bankAccountId="dep1"
        bankName="Security Deposit Trust"
        onClose={vi.fn()}
      />
    </QueryClientProvider>,
  );
}

const ADMIN_SESSION = {
  userId: 'user-1',
  name: 'Renée Calloway',
  email: 'renee@example.com',
  role: 'PMAdmin',
  orgId: 'org-1',
  orgName: 'Blue Ridge PM',
  mfaEnabled: true,
  mfaEnrollmentRequired: false,
};

const asAdmin = () => http.get('/api/auth/me', () => HttpResponse.json(ADMIN_SESSION));

const micrDetails = (overrides: Record<string, unknown> = {}) =>
  http.get('/api/refund-checks/micr/:bankAccountId', ({ params }) =>
    HttpResponse.json({
      bankAccountId: params.bankAccountId,
      stockKind: 'preprinted',
      routingNumberLast4: null,
      onUsAccountNumberLast4: null,
      micrOffsetXPoints: 0,
      micrOffsetYPoints: 0,
      ...overrides,
    }),
  );

const savedSettings = () =>
  http.get('/api/refund-checks/print-settings/:bankAccountId', ({ params }) =>
    HttpResponse.json({
      bankAccountId: params.bankAccountId,
      offsetXPoints: 4.5,
      offsetYPoints: -2,
    }),
  );

// Captured so assertions read plain mocks rather than unbound methods off `URL`.
const createObjectURL = vi.fn((_blob: Blob | MediaSource) => 'blob:test');
const revokeObjectURL = vi.fn((_url: string) => {});

beforeEach(() => {
  // Tests that care about the MICR details override this; the rest see an account with none saved.
  server.use(micrDetails());
  document.body.innerHTML = '';
  vi.clearAllMocks();
  globalThis.URL.createObjectURL = createObjectURL;
  globalThis.URL.revokeObjectURL = revokeObjectURL;
});

describe('CheckPrintSettingsDialog', () => {
  it('seeds the offsets from the saved settings', async () => {
    server.use(savedSettings());
    renderDialog();

    expect(await screen.findByLabelText('Horizontal offset (points)')).toHaveValue('4.5');
    expect(screen.getByLabelText('Vertical offset (points)')).toHaveValue('-2');
    expect(screen.getByLabelText('Horizontal offset (points)')).toHaveAccessibleDescription(
      'Positive moves right, negative moves left.',
    );
  });

  it('refuses an offset beyond one inch either way without saving', async () => {
    let saved = false;
    server.use(
      savedSettings(),
      http.put('/api/refund-checks/print-settings/:bankAccountId', () => {
        saved = true;
        return HttpResponse.json({});
      }),
    );
    renderDialog();

    const x = await screen.findByLabelText('Horizontal offset (points)');
    await userEvent.clear(x);
    await userEvent.type(x, '72.5');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('from −72 to 72');
    expect(x).toHaveAttribute('aria-invalid', 'true');
    expect(saved).toBe(false);

    const y = screen.getByLabelText('Vertical offset (points)');
    await userEvent.clear(x);
    await userEvent.type(x, '-72');
    await userEvent.clear(y);
    await userEvent.type(y, '-73');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('from −72 to 72');
    expect(y).toHaveAttribute('aria-invalid', 'true');
    expect(saved).toBe(false);
  });

  it('saves offsets at the bounds', async () => {
    let body: unknown;
    server.use(
      savedSettings(),
      http.put('/api/refund-checks/print-settings/:bankAccountId', async ({ request, params }) => {
        body = await request.json();
        return HttpResponse.json({
          bankAccountId: params.bankAccountId,
          offsetXPoints: 72,
          offsetYPoints: -72,
        });
      }),
    );
    renderDialog();

    const x = await screen.findByLabelText('Horizontal offset (points)');
    const y = screen.getByLabelText('Vertical offset (points)');
    await userEvent.clear(x);
    await userEvent.type(x, '72');
    await userEvent.clear(y);
    await userEvent.type(y, '-72');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByRole('status')).toHaveTextContent('Saved.');
    expect(body).toEqual({ offsetXPoints: 72, offsetYPoints: -72 });
  });

  it('saves unsaved offsets before printing the alignment page with them', async () => {
    const calls: string[] = [];
    server.use(
      savedSettings(),
      http.put('/api/refund-checks/print-settings/:bankAccountId', async ({ request, params }) => {
        calls.push('save');
        const body = (await request.json()) as { offsetXPoints: number; offsetYPoints: number };
        return HttpResponse.json({ bankAccountId: params.bankAccountId, ...body });
      }),
      http.post('/api/refund-checks/print-settings/:bankAccountId/alignment', () => {
        calls.push('alignment');
        return new HttpResponse(new Blob(['%PDF-1.7']), {
          headers: { 'Content-Type': 'application/pdf' },
        });
      }),
    );
    renderDialog();

    const x = await screen.findByLabelText('Horizontal offset (points)');
    await userEvent.clear(x);
    await userEvent.type(x, '6');
    await userEvent.click(screen.getByRole('button', { name: 'Print alignment page' }));

    await vi.waitFor(() => expect(calls).toEqual(['save', 'alignment']));
    expect(createObjectURL).toHaveBeenCalledTimes(1);
  });

  it('reports a failed settings read and blocks saving over it', async () => {
    server.use(
      http.get('/api/refund-checks/print-settings/:bankAccountId', () =>
        HttpResponse.json(
          { detail: 'Settings unavailable.', correlationId: 'beefbeefbeefbeefbeefbeefbeefbeef' },
          { status: 500 },
        ),
      ),
    );
    renderDialog();

    expect(await screen.findByText('Settings unavailable.')).toBeInTheDocument();
    expect(screen.getByText('Reference: beefbeefbeefbeefbeefbeefbeefbeef')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Print alignment page' })).toBeDisabled();
  });

  describe('MICR details for blank stock (#474)', () => {
    it('shows staff the last four digits only, read-only, and says who can change them', async () => {
      server.use(
        savedSettings(),
        micrDetails({
          stockKind: 'blank',
          routingNumberLast4: '0012',
          onUsAccountNumberLast4: '6789',
          micrOffsetXPoints: 1.5,
          micrOffsetYPoints: -2,
        }),
      );
      renderDialog();

      const micr = await screen.findByRole('group', { name: 'MICR details' });
      expect(await within(micr).findByText('Routing number ending 0012')).toBeInTheDocument();
      expect(within(micr).getByText('On-Us field ending 6789')).toBeInTheDocument();
      expect(
        within(micr).getByText('Blank stock — LeaseBook prints the MICR line'),
      ).toBeInTheDocument();
      expect(
        within(micr).getByText('Only an administrator can change the MICR details.'),
      ).toBeInTheDocument();
      // Until LeaseBook prints the MICR line, a blank-stock account prints nothing — and says so.
      expect(
        within(micr).getByText(
          'Checks on this account won’t print until LeaseBook can print the MICR line.',
        ),
      ).toBeInTheDocument();
      expect(within(micr).queryByRole('textbox')).toBeNull();
      expect(within(micr).queryByRole('button', { name: 'Save MICR details' })).toBeNull();
    });

    it('lets an admin replace the numbers, then clears them and keeps only the masked view', async () => {
      let body: unknown;
      server.use(
        asAdmin(),
        savedSettings(),
        http.put('/api/refund-checks/micr/:bankAccountId', async ({ request, params }) => {
          body = await request.json();
          return HttpResponse.json({
            bankAccountId: params.bankAccountId,
            stockKind: 'blank',
            routingNumberLast4: '0012',
            onUsAccountNumberLast4: '6789',
            micrOffsetXPoints: 0,
            micrOffsetYPoints: 0,
          });
        }),
      );
      const queryClient = renderDialog();

      const micr = await screen.findByRole('group', { name: 'MICR details' });
      expect(await within(micr).findByText('No routing number saved')).toBeInTheDocument();
      await userEvent.click(within(micr).getByRole('radio', { name: /Blank stock/ }));
      await userEvent.type(within(micr).getByLabelText('Routing number'), '111000012');
      await userEvent.type(within(micr).getByLabelText('On-Us field'), '123456789U');
      await userEvent.click(within(micr).getByRole('button', { name: 'Save MICR details' }));

      expect(await within(micr).findByText('Routing number ending 0012')).toBeInTheDocument();
      expect(body).toEqual({
        stockKind: 'blank',
        routingNumber: '111000012',
        onUsAccountNumber: '123456789U',
        micrOffsetXPoints: 0,
        micrOffsetYPoints: 0,
      });
      expect(within(micr).getByLabelText('Routing number')).toHaveValue('');
      expect(within(micr).getByLabelText('On-Us field')).toHaveValue('');

      // Nothing the client keeps holds the full numbers: not the query cache, not the mutation cache.
      const cached = JSON.stringify([
        queryClient
          .getQueryCache()
          .getAll()
          .map((q) => q.state.data),
        queryClient
          .getMutationCache()
          .getAll()
          .map((m) => m.state.variables),
      ]);
      expect(cached).not.toContain('111000012');
      expect(cached).not.toContain('123456789');
    });

    it('sends only what was typed: a blank number field keeps the saved one', async () => {
      let body: unknown;
      server.use(
        asAdmin(),
        savedSettings(),
        micrDetails({
          stockKind: 'blank',
          routingNumberLast4: '0012',
          onUsAccountNumberLast4: '6789',
        }),
        http.put('/api/refund-checks/micr/:bankAccountId', async ({ request, params }) => {
          body = await request.json();
          return HttpResponse.json({
            bankAccountId: params.bankAccountId,
            stockKind: 'blank',
            routingNumberLast4: '0012',
            onUsAccountNumberLast4: '6789',
            micrOffsetXPoints: 3,
            micrOffsetYPoints: 0,
          });
        }),
      );
      renderDialog();

      const micr = await screen.findByRole('group', { name: 'MICR details' });
      const x = await within(micr).findByLabelText('MICR line horizontal offset (points)');
      await userEvent.clear(x);
      await userEvent.type(x, '3');
      await userEvent.click(within(micr).getByRole('button', { name: 'Save MICR details' }));

      await vi.waitFor(() =>
        expect(body).toEqual({
          stockKind: 'blank',
          routingNumber: null,
          onUsAccountNumber: null,
          micrOffsetXPoints: 3,
          micrOffsetYPoints: 0,
        }),
      );
    });

    it('shows the server’s reason when it refuses the details, keeping what was typed', async () => {
      server.use(
        asAdmin(),
        savedSettings(),
        http.put('/api/refund-checks/micr/:bankAccountId', () =>
          HttpResponse.json(
            {
              code: 'validation_failed',
              detail: 'The routing number must be nine digits with a valid check digit.',
              correlationId: 'feedfeedfeedfeedfeedfeedfeedfeed',
            },
            { status: 400 },
          ),
        ),
      );
      renderDialog();

      const micr = await screen.findByRole('group', { name: 'MICR details' });
      await userEvent.type(await within(micr).findByLabelText('Routing number'), '111000013');
      await userEvent.click(within(micr).getByRole('button', { name: 'Save MICR details' }));

      expect(
        await within(micr).findByText(
          'The routing number must be nine digits with a valid check digit.',
        ),
      ).toBeInTheDocument();
      expect(within(micr).getByLabelText('Routing number')).toHaveValue('111000013');
    });

    it('reports a failed MICR read and offers no save over it', async () => {
      server.use(
        asAdmin(),
        savedSettings(),
        http.get('/api/refund-checks/micr/:bankAccountId', () =>
          HttpResponse.json(
            {
              detail: 'MICR details unavailable.',
              correlationId: 'cafecafecafecafecafecafecafecafe',
            },
            { status: 500 },
          ),
        ),
      );
      renderDialog();

      const micr = await screen.findByRole('group', { name: 'MICR details' });
      expect(await within(micr).findByText('MICR details unavailable.')).toBeInTheDocument();
      expect(
        within(micr).getByText('Reference: cafecafecafecafecafecafecafecafe'),
      ).toBeInTheDocument();
      expect(within(micr).queryByRole('button', { name: 'Save MICR details' })).toBeNull();
    });
  });
});
