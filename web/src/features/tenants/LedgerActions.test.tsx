import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import { ApplyModal } from './ApplyModal';
import { AuditDrawer } from './AuditDrawer';
import { VoidDialog } from './VoidDialog';

const BANKS = [
  {
    id: 'trust1',
    name: 'Operating Trust',
    institution: null,
    mask: null,
    purpose: 'trust',
    isActive: true,
  },
  {
    id: 'dep1',
    name: 'Deposit Trust',
    institution: null,
    mask: null,
    purpose: 'deposit',
    isActive: true,
  },
];

const csrf = () => http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 }));
const banksHandler = () => http.get('/api/settings/banks', () => HttpResponse.json(BANKS));

function renderWith(node: ReactNode) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(<QueryClientProvider client={queryClient}>{node}</QueryClientProvider>);
}

beforeEach(() => {
  document.body.innerHTML = '';
  vi.clearAllMocks();
});

describe('VoidDialog', () => {
  it('requires a reason before voiding', async () => {
    server.use(csrf());
    const onVoided = vi.fn();
    renderWith(<VoidDialog entryId="e1" onClose={vi.fn()} onVoided={onVoided} />);

    await userEvent.click(screen.getByRole('button', { name: 'Void entry' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/reason is required/i);
    expect(onVoided).not.toHaveBeenCalled();
  });

  it('posts a reversal with the reason', async () => {
    let body: Record<string, unknown> | undefined;
    server.use(
      csrf(),
      http.post('/api/accounting/entries/:entryId/void', async ({ request }) => {
        body = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json({ entryId: 'rev1' });
      }),
    );
    const onVoided = vi.fn();
    renderWith(<VoidDialog entryId="e1" onClose={vi.fn()} onVoided={onVoided} />);

    await userEvent.type(screen.getByLabelText('Reason'), 'entered in error');
    await userEvent.click(screen.getByRole('button', { name: 'Void entry' }));

    await vi.waitFor(() => expect(onVoided).toHaveBeenCalledWith('rev1'));
    expect(body).toMatchObject({ reason: 'entered in error' });
    expect(body?.sourceRef).toEqual(expect.any(String));
  });

  it('shows a friendly message when already reversed', async () => {
    server.use(
      csrf(),
      http.post('/api/accounting/entries/:entryId/void', () =>
        HttpResponse.json(
          { code: 'already_reversed', detail: 'This entry has already been voided.' },
          { status: 409 },
        ),
      ),
    );
    renderWith(<VoidDialog entryId="e1" onClose={vi.fn()} onVoided={vi.fn()} />);

    await userEvent.type(screen.getByLabelText('Reason'), 'oops');
    await userEvent.click(screen.getByRole('button', { name: 'Void entry' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/already been voided/i);
  });

  it('closes on Escape', async () => {
    server.use(csrf());
    const onClose = vi.fn();
    renderWith(<VoidDialog entryId="e1" onClose={onClose} onVoided={vi.fn()} />);
    await userEvent.keyboard('{Escape}');
    expect(onClose).toHaveBeenCalled();
  });
});

describe('ApplyModal', () => {
  it('applies a deposit against charges', async () => {
    let body: Record<string, unknown> | undefined;
    server.use(
      csrf(),
      banksHandler(),
      http.post('/api/accounting/tenants/:tenantId/deposit-applications', async ({ request }) => {
        body = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json({ entryId: 'app1' });
      }),
    );
    const onApplied = vi.fn();
    renderWith(
      <ApplyModal tenantId="t1" initialKind="deposit" onClose={vi.fn()} onApplied={onApplied} />,
    );

    await screen.findByText(/From Deposit Trust/); // banks loaded
    await userEvent.type(screen.getByLabelText('Amount'), '1000');
    await userEvent.click(screen.getByRole('button', { name: 'Apply' }));

    await vi.waitFor(() => expect(onApplied).toHaveBeenCalledWith('app1'));
    expect(body).toMatchObject({
      tenantId: 't1',
      amount: 1000,
      target: 'against-charges',
      depositBankId: 'dep1',
      operatingBankId: 'trust1',
    });
  });

  it('warns and stays open when the apply exceeds the open receivable', async () => {
    server.use(
      csrf(),
      banksHandler(),
      http.post('/api/accounting/tenants/:tenantId/deposit-applications', () =>
        HttpResponse.json(
          {
            code: 'insufficient_receivable',
            detail:
              'Deposit application of 1200.00 exceeds the 1000.00 currently owed by this tenant.',
          },
          { status: 409 },
        ),
      ),
    );
    const onApplied = vi.fn();
    renderWith(
      <ApplyModal tenantId="t1" initialKind="deposit" onClose={vi.fn()} onApplied={onApplied} />,
    );

    await screen.findByText(/From Deposit Trust/);
    await userEvent.type(screen.getByLabelText('Amount'), '1200');
    await userEvent.click(screen.getByRole('button', { name: 'Apply' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      /exceeds the 1000.00 currently owed/i,
    );
    expect(onApplied).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Amount')).toBeInTheDocument(); // still open
  });

  it('applies a prepayment through its endpoint', async () => {
    let hit = false;
    server.use(
      csrf(),
      banksHandler(),
      http.post('/api/accounting/tenants/:tenantId/prepayment-applications', () => {
        hit = true;
        return HttpResponse.json({ entryId: 'pp1' });
      }),
    );
    const onApplied = vi.fn();
    renderWith(
      <ApplyModal tenantId="t1" initialKind="prepayment" onClose={vi.fn()} onApplied={onApplied} />,
    );

    await screen.findByText(/From Operating Trust/);
    await userEvent.type(screen.getByLabelText('Amount'), '300');
    await userEvent.click(screen.getByRole('button', { name: 'Apply' }));

    await vi.waitFor(() => expect(hit).toBe(true));
    expect(onApplied).toHaveBeenCalledWith('pp1');
  });
});

describe('AuditDrawer', () => {
  it('renders the audit rows', async () => {
    server.use(
      http.get('/api/accounting/entries/:entryId/audit', () =>
        HttpResponse.json({
          rows: [
            {
              occurredAt: '2026-02-02T10:00:00Z',
              action: 'insert',
              actorName: 'Renée Calloway',
              actorEmail: 'renee@x.example',
            },
            {
              occurredAt: '2026-02-01T10:00:00Z',
              action: 'insert',
              actorName: 'System',
              actorEmail: null,
            },
          ],
        }),
      ),
    );
    renderWith(<AuditDrawer entryId="e1" onClose={vi.fn()} />);

    expect(await screen.findByText('Renée Calloway')).toBeInTheDocument();
    expect(screen.getByText('System')).toBeInTheDocument();
  });

  it('shows an empty state with no history', async () => {
    server.use(
      http.get('/api/accounting/entries/:entryId/audit', () => HttpResponse.json({ rows: [] })),
    );
    renderWith(<AuditDrawer entryId="e1" onClose={vi.fn()} />);
    expect(await screen.findByText('No history yet')).toBeInTheDocument();
  });
});
