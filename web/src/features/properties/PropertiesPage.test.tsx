import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { beforeEach, describe, expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { PropertiesPage } from './PropertiesPage';

const PROPERTIES = {
  items: [
    {
      id: 'p1',
      address: '412 Oakmont Ave',
      city: 'Raleigh',
      ownerName: 'Hargrove Family Trust',
      units: 2,
      occupied: 2,
    },
  ],
  total: 1,
};

const OWNERS = { items: [{ id: 'o1', name: 'Hargrove Family Trust' }], total: 1 };

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      { path: '/properties', element: <PropertiesPage /> },
      { path: '/properties/:id', element: <div>property page</div> },
    ],
    { initialEntries: ['/properties'] },
  );
  render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

async function openNewProperty() {
  await userEvent.click(await screen.findByRole('button', { name: /new property/i }));
}

describe('NewPropertyModal when the owner list cannot load', () => {
  beforeEach(() => {
    document.body.innerHTML = '';
  });

  it('explains the failure instead of showing an empty owner selector', async () => {
    let created = false;
    server.use(
      http.get('/api/directory/properties', () => HttpResponse.json(PROPERTIES)),
      http.get('/api/directory/owners', () => new HttpResponse(null, { status: 503 })),
      http.post('/api/directory/properties', () => {
        created = true;
        return HttpResponse.json({ id: 'p2' });
      }),
    );
    renderPage();
    await openNewProperty();

    // The failure is stated, and it does not read as "this org has no owners".
    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.queryByText(/add an owner first/i)).toBeNull();

    // A property cannot be created without a real owner, so the create path stays shut on both
    // routes into it — the button and the form's own submit.
    expect(screen.getByRole('button', { name: /create property/i })).toBeDisabled();
    await userEvent.type(screen.getByLabelText('Address'), '9 Cardinal Ct{Enter}');
    expect(created).toBe(false);
  });

  it('distinguishes a successful empty list, which is a setup step, not a failure', async () => {
    server.use(
      http.get('/api/directory/properties', () => HttpResponse.json(PROPERTIES)),
      http.get('/api/directory/owners', () => HttpResponse.json({ items: [], total: 0 })),
    );
    renderPage();
    await openNewProperty();

    expect(await screen.findByText(/add an owner first/i)).toBeInTheDocument();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('keeps entered fields across a retry and recovers', async () => {
    let attempt = 0;
    server.use(
      http.get('/api/directory/properties', () => HttpResponse.json(PROPERTIES)),
      http.get('/api/directory/owners', () => {
        attempt += 1;
        return attempt === 1 ? new HttpResponse(null, { status: 503 }) : HttpResponse.json(OWNERS);
      }),
    );
    renderPage();
    await openNewProperty();

    await userEvent.type(await screen.findByLabelText('Address'), '9 Cardinal Ct');
    await userEvent.click(screen.getByRole('button', { name: /retry/i }));

    // Retrying an auxiliary read must not cost the operator the form they were filling in.
    expect(
      await screen.findByRole('option', { name: 'Hargrove Family Trust' }),
    ).toBeInTheDocument();
    expect(screen.getByLabelText('Address')).toHaveValue('9 Cardinal Ct');
  });
});
