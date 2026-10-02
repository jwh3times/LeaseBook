import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import { CheckPrintSettingsDialog } from './CheckPrintSettingsDialog';

function renderDialog() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
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
});
