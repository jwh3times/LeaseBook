import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router';
import { expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { PortalAccessPanel } from './PortalAccessPanel';

function renderPanel() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <PortalAccessPanel persona="tenant" targetId="tenant-a" />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

it('reports an unavailable access list without claiming nobody has access', async () => {
  server.use(
    http.get('/api/portal-access/tenant/tenant-a', () =>
      HttpResponse.json(
        {
          code: 'portal_access_unavailable',
          detail: 'Portal access could not be loaded.',
          correlationId: 'portal-read-reference',
        },
        { status: 503 },
      ),
    ),
  );
  renderPanel();
  expect(await screen.findByText('Portal access could not be loaded.')).toBeInTheDocument();
  expect(screen.getByText('Reference: portal-read-reference')).toBeInTheDocument();
  expect(screen.queryByText('No active portal users.')).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Send invitation' })).not.toBeInTheDocument();
});

it('refreshes queued delivery until a failure exposes the retry action', async () => {
  let reads = 0;
  server.use(
    http.get('/api/portal-access/tenant/tenant-a', () =>
      HttpResponse.json({
        canManage: true,
        users: [],
        invitations: [
          {
            id: 'invite-a',
            email: 'delayed@example.test',
            status: 'pending',
            deliveryStatus: ++reads === 1 ? 'pending' : 'failed',
            expiresAt: '2026-10-03T12:00:00Z',
          },
        ],
      }),
    ),
  );
  renderPanel();
  expect(await screen.findByText('Queued for test inbox')).toBeInTheDocument();
  expect(
    await screen.findByRole(
      'button',
      { name: /^Retry test delivery for delayed@example\.test,/ },
      { timeout: 4000 },
    ),
  ).toBeInTheDocument();
});

it.each([
  ['Replace invitation', 'replace'],
  ['Cancel invitation', 'cancel'],
  ['Retry test delivery', 'retry'],
])('offers %s for a pending invitation and reports a failed write', async (label, action) => {
  server.use(
    http.get('/api/portal-access/tenant/tenant-a', () =>
      HttpResponse.json({
        canManage: true,
        users: [],
        invitations: [
          {
            id: 'invite-a',
            email: 'pending@example.test',
            status: 'pending',
            deliveryStatus: 'failed',
            expiresAt: '2026-10-03T12:00:00Z',
          },
        ],
      }),
    ),
    http.post(`/api/portal-access/invitations/invite-a/${action}`, () =>
      HttpResponse.json(
        {
          code: 'portal_access_denied',
          detail: 'Only administrators can manage portal access.',
          correlationId: 'permission-changed',
        },
        { status: 403 },
      ),
    ),
  );
  renderPanel();
  await userEvent.click(
    await screen.findByRole('button', {
      name: new RegExp(`^${label} for pending@example\\.test,`),
    }),
  );
  expect(await screen.findByText('Reference: permission-changed')).toBeInTheDocument();
  expect(screen.getByText('Only administrators can manage portal access.')).toBeInTheDocument();
});

// #571: a screen reader lists the buttons by name, so each invitation's buttons say which invitation
// they act on. The name starts with what the button shows (WCAG 2.5.3) and carries the address. The
// server lets one address hold two open invitations, so the expiry each row shows tells those apart.
it('names each invitation action by its address and expiry, keeping the visible label', async () => {
  const invitation = { status: 'pending', deliveryStatus: 'failed' };
  server.use(
    http.get('/api/portal-access/tenant/tenant-a', () =>
      HttpResponse.json({
        canManage: true,
        users: [],
        invitations: [
          { ...invitation, id: 'a1', email: 'ana@example.test', expiresAt: '2026-10-03T12:00:00Z' },
          { ...invitation, id: 'b1', email: 'ben@example.test', expiresAt: '2026-10-03T12:00:00Z' },
          { ...invitation, id: 'a2', email: 'ana@example.test', expiresAt: '2026-10-04T09:30:00Z' },
        ],
      }),
    ),
  );
  renderPanel();
  await screen.findByText('ben@example.test');

  const buttons = ['Replace invitation', 'Cancel invitation', 'Retry test delivery'].flatMap(
    (label) => {
      const found = screen.getAllByRole('button', { name: new RegExp(`^${label} for `) });
      for (const button of found) expect(button).toHaveTextContent(label);
      return found;
    },
  );
  expect(buttons).toHaveLength(9);
  for (const email of ['ana@example.test', 'ben@example.test']) {
    const forEmail = buttons.filter((button) =>
      button.getAttribute('aria-label')?.startsWith(`Replace invitation for ${email},`),
    );
    expect(forEmail).toHaveLength(email.startsWith('ana') ? 2 : 1);
  }
  const names = screen
    .getAllByRole('button')
    .map((button) => button.getAttribute('aria-label') ?? button.textContent);
  expect(new Set(names).size).toBe(names.length);
});

it('revokes an active user only after confirmation and refreshes the access list', async () => {
  let revoked = false;
  server.use(
    http.get('/api/portal-access/tenant/tenant-a', () =>
      HttpResponse.json({
        canManage: true,
        invitations: [],
        users: revoked
          ? []
          : [{ userId: 'user-a', email: 'active@example.test', displayName: 'Active User' }],
      }),
    ),
    http.post('/api/portal-access/tenant/tenant-a/users/user-a/revoke', () => {
      revoked = true;
      return HttpResponse.json({ success: true });
    }),
  );
  renderPanel();
  const user = userEvent.setup();
  await user.click(
    await screen.findByRole('button', { name: 'Revoke access for active@example.test' }),
  );
  expect(revoked).toBe(false);
  await user.click(
    within(screen.getByRole('dialog')).getByRole('button', { name: 'Revoke access' }),
  );
  expect(await screen.findByText('No active portal users.')).toBeInTheDocument();
});

it('invites an explicit email and shows pending test delivery without claiming email was sent', async () => {
  let invited = false;
  server.use(
    http.get('/api/portal-access/tenant/tenant-a', () =>
      HttpResponse.json({
        canManage: true,
        users: [],
        invitations: invited
          ? [
              {
                id: 'invite-a',
                email: 'person@example.test',
                status: 'pending',
                deliveryStatus: 'pending',
                expiresAt: '2026-10-03T12:00:00Z',
              },
            ]
          : [],
      }),
    ),
    http.post('/api/portal-access/tenant/tenant-a', async ({ request }) => {
      expect(await request.json()).toEqual({ email: 'person@example.test' });
      invited = true;
      return HttpResponse.json({ id: 'invite-a' });
    }),
  );
  renderPanel();
  const user = userEvent.setup();
  await user.type(await screen.findByLabelText('Invitation email'), 'person@example.test');
  await user.click(screen.getByRole('button', { name: 'Send invitation' }));
  expect(await screen.findByText('person@example.test')).toBeInTheDocument();
  expect(screen.getByText('Queued for test inbox')).toBeInTheDocument();
  expect(screen.queryByText('Email sent')).not.toBeInTheDocument();
});
