import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { beforeEach, describe, expect, it } from 'vitest';
import { RecordNavProvider } from '@/components/recordNav';
import { server } from '@/test/mocks/server';
import { OwnerDetailPage } from './OwnerDetailPage';

const OWNER = {
  id: 'o1',
  name: 'Hargrove Family Trust',
  defaultMgmtFeeBps: 800,
  contact: { email: 'trust@example.test', phone: '828-555-0142' },
  operating: 4200,
  deposits: 1450,
  total: 5650,
  reserveAmount: 500,
  properties: [{ id: 'p1', address: '412 Oakmont Ave', city: 'Raleigh', units: 2, occupied: 2 }],
};

const REFERENCE = 'a1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4';

function renderOwner() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      { path: '/owners', element: <div>owners index</div> },
      { path: '/owners/:id', element: <OwnerDetailPage /> },
    ],
    { initialEntries: ['/owners/o1'] },
  );
  render(
    <QueryClientProvider client={queryClient}>
      <RecordNavProvider>
        <RouterProvider router={router} />
      </RecordNavProvider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  document.body.innerHTML = '';
});

// DetailPage is the shared scaffold behind every /owners/:id and /properties/:id route, so its
// error branch is exercised here through a real one rather than against a fabricated query.
describe('DetailPage read errors, via OwnerDetailPage', () => {
  it('surfaces the server message and support reference instead of "Record not found"', async () => {
    server.use(
      http.get('/api/directory/owners/:id', () =>
        HttpResponse.json(
          { detail: 'The owner service is unavailable.', correlationId: REFERENCE },
          { status: 503 },
        ),
      ),
    );
    renderOwner();

    expect(await screen.findByText('Couldn’t load this record')).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent('The owner service is unavailable.');
    expect(screen.getByText(`Reference: ${REFERENCE}`)).toBeInTheDocument();

    // A failed read is not evidence that the record is gone, and telling the operator it is sends
    // them to look for a deletion that never happened.
    expect(screen.queryByText('Record not found')).toBeNull();
  });

  it('still says "Record not found" for a 404, which retrying cannot fix', async () => {
    server.use(
      http.get('/api/directory/owners/:id', () => new HttpResponse(null, { status: 404 })),
    );
    renderOwner();

    expect(await screen.findByText('Record not found')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
  });

  it('recovers in place when the retry succeeds', async () => {
    let attempt = 0;
    server.use(
      http.get('/api/directory/owners/:id', () => {
        attempt += 1;
        return attempt === 1 ? new HttpResponse(null, { status: 500 }) : HttpResponse.json(OWNER);
      }),
    );
    renderOwner();

    await userEvent.click(await screen.findByRole('button', { name: 'Retry' }));

    expect(await screen.findByText('Hargrove Family Trust')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
