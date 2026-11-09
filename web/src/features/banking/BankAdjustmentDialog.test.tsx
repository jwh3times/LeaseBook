import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { dateInZone } from '@/lib/orgCalendar';
import { ORG_SETTINGS } from '@/test/mocks/handlers';
import { server } from '@/test/mocks/server';
import { BankAdjustmentDialog, type BankAdjustmentDialogProps } from './BankAdjustmentDialog';

const TRUST = { bankAccountId: 'trust1', name: 'Operating Trust', isTrust: true };
const DEPOSIT = { bankAccountId: 'dep1', name: 'Deposit Trust', isTrust: true };
const RESERVE = { bankAccountId: 'res1', name: 'Reserve Trust', isTrust: true };
const OPERATING = { bankAccountId: 'op1', name: 'Management Operating', isTrust: false };
const row = (bank: { bankAccountId: string; name: string; isTrust: boolean }) => ({
  ...bank,
  book: 1000,
  cleared: 1000,
  uncleared: 0,
  unclearedCount: 0,
});

const REFERENCE = '0123456789abcdef0123456789abcdef';
const problem = (status: number, code: string, detail: string) =>
  HttpResponse.json(
    { title: code, status, detail, code, correlationId: REFERENCE },
    { status, headers: { 'content-type': 'application/problem+json' } },
  );

type Posted = { url: string; body: Record<string, unknown> };

function serve(opts: { banks?: (typeof TRUST)[]; respond?: () => Response } = {}) {
  const posted: Posted[] = [];
  server.use(
    http.get('/api/accounting/banks/balances', () =>
      HttpResponse.json({ rows: (opts.banks ?? [TRUST, DEPOSIT, OPERATING]).map(row) }),
    ),
    http.post('/api/accounting/banks/:id/adjustments', async ({ request }) => {
      posted.push({ url: new URL(request.url).pathname, body: (await request.json()) as never });
      return opts.respond?.() ?? HttpResponse.json({ entryId: 'e1' });
    }),
  );
  return posted;
}

function renderDialog(props: Partial<BankAdjustmentDialogProps> = {}) {
  const onPosted = vi.fn();
  const onClose = vi.fn();
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  render(
    <QueryClientProvider client={queryClient}>
      <BankAdjustmentDialog
        bankAccountId="trust1"
        bankName="Operating Trust"
        isTrust
        onClose={onClose}
        onPosted={onPosted}
        {...props}
      />
    </QueryClientProvider>,
  );
  return { onPosted, onClose };
}

const orgToday = () => dateInZone(ORG_SETTINGS.timeZone);
const amountField = () => screen.getByLabelText('Amount');
const saveFee = () => screen.getByRole('button', { name: 'Record fee' });

describe('BankAdjustmentDialog', () => {
  it('records a bank fee dated the organization’s today and reports what it posted', async () => {
    const posted = serve();
    const { onPosted } = renderDialog();
    const user = userEvent.setup();

    expect(await screen.findByDisplayValue(orgToday())).toBe(screen.getByLabelText('Date'));
    await user.type(amountField(), '25.5');
    await user.type(screen.getByLabelText('Description'), 'Wire fee');
    await user.type(screen.getByLabelText(/Internal note/), 'per the June statement');
    await user.click(saveFee());

    await vi.waitFor(() => expect(onPosted).toHaveBeenCalledTimes(1));
    expect(onPosted).toHaveBeenCalledWith({ kind: 'fee', amount: 25.5, toBankAccountId: null });
    expect(posted).toHaveLength(1);
    expect(posted[0]?.url).toBe('/api/accounting/banks/trust1/adjustments');
    const { sourceRef, ...body } = posted[0]?.body ?? {};
    expect(body).toEqual({
      bankAccountId: 'trust1',
      kind: 'fee',
      amount: 25.5,
      date: orgToday(),
      description: 'Wire fee',
      toBankAccountId: null,
      internalNote: 'per the June statement',
    });
    expect(sourceRef).toMatch(/^bank-adjustment:[0-9a-f-]{36}$/);
  });

  it('dates the fee in the organization’s zone, not the viewer’s', async () => {
    // 06:30 UTC on 1 Nov 2026 is still 31 Oct in Los Angeles, and already 1 Nov in New York.
    vi.setSystemTime(new Date('2026-11-01T06:30:00Z'));
    server.use(
      http.get('/api/settings/org', () =>
        HttpResponse.json({ ...ORG_SETTINGS, timeZone: 'America/Los_Angeles' }),
      ),
    );
    serve();
    renderDialog();

    expect(await screen.findByDisplayValue('2026-10-31')).toBe(screen.getByLabelText('Date'));
    expect(screen.getByLabelText('Date')).toHaveAttribute('max', '2026-10-31');
  });

  it('never posts from Enter in a field', async () => {
    const posted = serve();
    renderDialog();
    const user = userEvent.setup();
    await screen.findByDisplayValue(orgToday());

    await user.type(amountField(), '25{Enter}');
    await user.type(screen.getByLabelText('Description'), 'Fee{Enter}');
    await user.click(screen.getByLabelText('Date'));
    await user.keyboard('{Enter}');

    expect(posted).toHaveLength(0);
  });

  it('runs the fields’ own constraints before sending anything', async () => {
    const posted = serve();
    renderDialog();
    const user = userEvent.setup();
    await screen.findByDisplayValue(orgToday());

    // Empty, then more than two decimal places: the browser refuses both and nothing is sent.
    await user.click(saveFee());
    expect(amountField()).toHaveFocus();
    await user.type(amountField(), '25.505');
    await user.click(saveFee());
    expect(posted).toHaveLength(0);

    await user.clear(amountField());
    await user.type(amountField(), '0');
    await user.click(saveFee());
    expect(await screen.findByText(/greater than zero/)).toBeInTheDocument();
    expect(posted).toHaveLength(0);

    await user.clear(amountField());
    await user.type(amountField(), '25');
    await user.click(saveFee());
    await vi.waitFor(() => expect(posted).toHaveLength(1));
  });

  it('refuses a date after the organization’s today in the field, before sending anything', async () => {
    const posted = serve();
    renderDialog();
    const user = userEvent.setup();
    await screen.findByDisplayValue(orgToday());
    const [y, m, d] = orgToday().split('-').map(Number);
    const tomorrow = new Date(Date.UTC(y ?? 0, (m ?? 1) - 1, (d ?? 1) + 1))
      .toISOString()
      .slice(0, 10);

    await user.type(amountField(), '25');
    await user.clear(screen.getByLabelText('Date'));
    await user.type(screen.getByLabelText('Date'), tomorrow);
    await user.click(saveFee());

    expect(screen.getByLabelText('Date')).toHaveFocus();
    expect(posted).toHaveLength(0);
  });

  it('shows a held-fees refusal beside the amount in the server’s words, with its reference', async () => {
    const detail =
      'This account holds only 10.00 in management fees on or after the fee’s date, less than the 25.00 fee, so nothing was posted.';
    serve({ respond: () => problem(409, 'pm_fees_insufficient', detail) });
    const { onPosted } = renderDialog();
    const user = userEvent.setup();
    await screen.findByDisplayValue(orgToday());

    await user.type(amountField(), '25');
    await user.click(saveFee());

    expect(await screen.findByText(detail)).toBeInTheDocument();
    expect(screen.getByText(REFERENCE, { exact: false })).toBeInTheDocument();
    expect(amountField()).toHaveAttribute('aria-invalid', 'true');
    expect(amountField().getAttribute('aria-describedby')).toMatch(/-error$/);
    expect(onPosted).not.toHaveBeenCalled();
  });

  it('shows a refusal into a reconciled month against the date', async () => {
    serve({ respond: () => problem(409, 'account_period_locked', 'This month is reconciled.') });
    renderDialog();
    const user = userEvent.setup();
    await screen.findByDisplayValue(orgToday());

    await user.type(amountField(), '25');
    await user.click(saveFee());

    expect(await screen.findByText(/reconciled/i)).toBeInTheDocument();
    expect(screen.getByLabelText('Date')).toHaveAttribute('aria-invalid', 'true');
  });

  it('treats its own source reference coming back as a duplicate as already posted', async () => {
    serve({ respond: () => problem(409, 'duplicate_source_ref', 'Already posted.') });
    const { onPosted } = renderDialog();
    const user = userEvent.setup();
    await screen.findByDisplayValue(orgToday());

    await user.type(amountField(), '25');
    await user.click(saveFee());

    await vi.waitFor(() => expect(onPosted).toHaveBeenCalledTimes(1));
  });

  it('transfers only to the organization’s other trust accounts', async () => {
    const posted = serve({ banks: [TRUST, DEPOSIT, RESERVE, OPERATING] });
    const { onPosted } = renderDialog();
    const user = userEvent.setup();
    await screen.findByDisplayValue(orgToday());

    await user.click(screen.getByRole('radio', { name: 'Transfer to another trust account' }));
    const to = await screen.findByLabelText('To account');
    expect(Array.from(to.querySelectorAll('option'), (option) => option.textContent)).toEqual([
      'Choose a trust account',
      'Deposit Trust',
      'Reserve Trust',
    ]);

    await user.type(amountField(), '12.34');
    const save = screen.getByRole('button', { name: 'Record transfer' });
    await user.click(save);
    expect(posted).toHaveLength(0); // no destination chosen yet

    await user.selectOptions(to, 'Reserve Trust');
    await user.click(save);

    await vi.waitFor(() => expect(onPosted).toHaveBeenCalledTimes(1));
    expect(onPosted).toHaveBeenCalledWith({
      kind: 'transfer',
      amount: 12.34,
      toBankAccountId: 'res1',
    });
    expect(posted[0]?.body).toMatchObject({ kind: 'transfer', toBankAccountId: 'res1' });
  });

  it('chooses the only other trust account without asking', async () => {
    const posted = serve({ banks: [TRUST, DEPOSIT, OPERATING] });
    renderDialog();
    const user = userEvent.setup();
    await screen.findByDisplayValue(orgToday());

    await user.click(screen.getByRole('radio', { name: 'Transfer to another trust account' }));
    expect(await screen.findByLabelText('To account')).toHaveValue('dep1');
    await user.type(amountField(), '5');
    await user.click(screen.getByRole('button', { name: 'Record transfer' }));

    await vi.waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0]?.body).toMatchObject({ toBankAccountId: 'dep1' });
  });

  it('says so, and posts nothing, when there is no other trust account', async () => {
    serve({ banks: [TRUST, OPERATING] });
    renderDialog();
    const user = userEvent.setup();
    await screen.findByDisplayValue(orgToday());

    await user.click(screen.getByRole('radio', { name: 'Transfer to another trust account' }));

    expect(await screen.findByText(/no other trust account/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Record transfer' })).toBeDisabled();
  });

  it('blocks a transfer while the trust accounts cannot be read', async () => {
    serve();
    server.use(
      http.get('/api/accounting/banks/balances', () =>
        problem(500, 'internal_error', 'The accounts could not be read.'),
      ),
    );
    renderDialog();
    const user = userEvent.setup();
    await screen.findByDisplayValue(orgToday());

    await user.click(screen.getByRole('radio', { name: 'Transfer to another trust account' }));

    expect(await screen.findByText(REFERENCE, { exact: false })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /retry/i })).toBeInTheDocument();
    expect(screen.queryByLabelText('To account')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Record transfer' })).toBeDisabled();
  });

  it('records a PM contribution into a trust account, with no destination', async () => {
    const posted = serve();
    const { onPosted } = renderDialog();
    const user = userEvent.setup();
    await screen.findByDisplayValue(orgToday());

    await user.click(screen.getByRole('radio', { name: 'PM contribution' }));
    expect(screen.getByRole('dialog', { name: 'Record a PM contribution' })).toBeInTheDocument();
    expect(screen.getByText(/The property manager’s own money/)).toBeInTheDocument();
    expect(screen.queryByLabelText('To account')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Description')).toHaveAttribute('placeholder', 'PM contribution');
    await user.type(amountField(), '25');
    await user.type(screen.getByLabelText(/Internal note/), 'bank charged the fee on the 3rd');
    await user.click(screen.getByRole('button', { name: 'Record contribution' }));

    await vi.waitFor(() => expect(onPosted).toHaveBeenCalledTimes(1));
    expect(onPosted).toHaveBeenCalledWith({
      kind: 'contribution',
      amount: 25,
      toBankAccountId: null,
    });
    const { sourceRef, ...body } = posted[0]?.body ?? {};
    expect(sourceRef).toMatch(/^bank-adjustment:[0-9a-f-]{36}$/);
    expect(body).toEqual({
      bankAccountId: 'trust1',
      kind: 'contribution',
      amount: 25,
      date: orgToday(),
      description: null,
      toBankAccountId: null,
      internalNote: 'bank charged the fee on the 3rd',
    });
  });

  it('shows a contribution refused for an account that is not a trust account in the server’s words', async () => {
    const detail =
      'A PM contribution is paid into a trust account, and this account is not a trust account.';
    serve({ respond: () => problem(422, 'contribution_bank_not_trust', detail) });
    const { onPosted } = renderDialog();
    const user = userEvent.setup();
    await screen.findByDisplayValue(orgToday());

    await user.click(screen.getByRole('radio', { name: 'PM contribution' }));
    await user.type(amountField(), '25');
    await user.click(screen.getByRole('button', { name: 'Record contribution' }));

    expect(await screen.findByText(detail)).toBeInTheDocument();
    expect(screen.getByText(REFERENCE, { exact: false })).toBeInTheDocument();
    expect(onPosted).not.toHaveBeenCalled();
  });

  it('offers no transfer from an account that is not a trust account', async () => {
    serve();
    renderDialog({ bankAccountId: 'op1', bankName: 'Management Operating', isTrust: false });
    await screen.findByDisplayValue(orgToday());

    expect(screen.queryByRole('radio')).not.toBeInTheDocument();
    expect(screen.queryByText(/Transfer/)).not.toBeInTheDocument();
    expect(screen.queryByText(/contribution/i)).not.toBeInTheDocument();
    expect(saveFee()).toBeInTheDocument();
  });

  it('blocks the write, with the reason and its reference, while the organization’s today is unknown', async () => {
    serve();
    server.use(
      http.get('/api/settings/org', () =>
        problem(500, 'internal_error', 'The settings could not be read.'),
      ),
    );
    renderDialog();

    expect(await screen.findByText(REFERENCE, { exact: false })).toBeInTheDocument();
    expect(saveFee()).toBeDisabled();
    expect(screen.getByLabelText('Date')).toHaveValue('');
  });
});
