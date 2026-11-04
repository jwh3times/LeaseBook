import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { beforeEach, describe, expect, it } from 'vitest';
import { RecordNavProvider } from '@/components/recordNav';
import { server } from '@/test/mocks/server';
import { OwnersPage } from './OwnersPage';

/** The create requests the dialog sent, in order. */
let posts: Record<string, unknown>[];

beforeEach(() => {
  posts = [];
  server.use(
    http.get('/api/directory/owners', () =>
      HttpResponse.json({ items: [], total: 0, page: 1, pageSize: 200 }),
    ),
    http.post('/api/directory/owners', async ({ request }) => {
      posts.push((await request.json()) as Record<string, unknown>);
      return HttpResponse.json({ id: 'oNew' });
    }),
  );
});

async function openNewOwner() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      { path: '/owners', element: <OwnersPage /> },
      { path: '/owners/:id', element: <div>owner page</div> },
    ],
    { initialEntries: ['/owners'] },
  );
  render(
    <QueryClientProvider client={queryClient}>
      <RecordNavProvider>
        <RouterProvider router={router} />
      </RecordNavProvider>
    </QueryClientProvider>,
  );
  await userEvent.click(await screen.findByRole('button', { name: 'New owner' }));
  await userEvent.type(screen.getByLabelText('Name'), 'Hargrove Family Trust');
}

async function setFee(value: string) {
  await userEvent.clear(screen.getByLabelText('Mgmt fee %'));
  await userEvent.type(screen.getByLabelText('Mgmt fee %'), value);
}

// #561: Enter in a field submits the dialog, and the fields' own constraints run before any request.
describe('New owner dialog', () => {
  it('creates the owner once when Enter is pressed in a field, taking a two-decimal fee', async () => {
    await openNewOwner();
    await setFee('8.25');
    await userEvent.type(screen.getByLabelText('Email'), 'trust@example.test{Enter}');

    expect(await screen.findByText('owner page')).toBeInTheDocument();
    expect(posts).toHaveLength(1);
    expect(posts[0]).toMatchObject({
      name: 'Hargrove Family Trust',
      contactEmail: 'trust@example.test',
      defaultMgmtFeeBps: 825,
    });
  });

  // A refused value is corrected and sent, so the one request that reaches the server must carry the
  // correction: a request the refusal let through would have been sent first.
  it('sends nothing for a fee over 100%, from Enter or the button, and marks the fee field', async () => {
    await openNewOwner();
    await setFee('150');
    await userEvent.keyboard('{Enter}');
    await userEvent.click(screen.getByRole('button', { name: 'Create owner' }));

    expect(screen.getByLabelText('Mgmt fee %')).toBeInvalid();
    expect(screen.getByRole('dialog', { name: 'New owner' })).toBeInTheDocument();

    await setFee('15');
    await userEvent.click(screen.getByRole('button', { name: 'Create owner' }));
    expect(await screen.findByText('owner page')).toBeInTheDocument();
    expect(posts).toEqual([expect.objectContaining({ defaultMgmtFeeBps: 1500 })]);
  });

  it('sends nothing for a malformed email, and marks the email field', async () => {
    await openNewOwner();
    await userEvent.type(screen.getByLabelText('Email'), 'not-an-email{Enter}');
    await userEvent.click(screen.getByRole('button', { name: 'Create owner' }));

    expect(screen.getByLabelText('Email')).toBeInvalid();

    await userEvent.clear(screen.getByLabelText('Email'));
    await userEvent.type(screen.getByLabelText('Email'), 'trust@example.test{Enter}');
    expect(await screen.findByText('owner page')).toBeInTheDocument();
    expect(posts).toEqual([expect.objectContaining({ contactEmail: 'trust@example.test' })]);
  });
});
