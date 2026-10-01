import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router';
import { expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { PortalTestInboxPage } from './PortalTestInboxPage';

it('labels local messages as test delivery and keeps links out of the query cache', async () => {
  server.use(
    http.get('/api/portal-access/test-inbox', () =>
      HttpResponse.json([
        {
          invitationId: 'invite',
          email: 'person@example.test',
          acceptUrl: 'http://localhost:5373/portal/enroll#token=test-secret',
        },
      ]),
    ),
  );
  const client = new QueryClient();
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <PortalTestInboxPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  expect(
    await screen.findByRole('link', { name: 'Open invitation for person@example.test' }),
  ).toHaveAttribute('href', 'http://localhost:5373/portal/enroll#token=test-secret');
  expect(
    screen.getByText('Development only. These invitations have not been emailed.'),
  ).toBeInTheDocument();
  expect(JSON.stringify(client.getQueryCache().getAll())).not.toContain('test-secret');
});
