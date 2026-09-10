import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { beforeEach, describe, expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { StatementPage } from './ReportsPage';

const REFERENCE = 'e1e2e3e4e1e2e3e4e1e2e3e4e1e2e3e4';

function renderStatement() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter([{ path: '/owners/:id', element: <StatementPage /> }], {
    initialEntries: ['/owners/owner-1'],
  });
  render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  document.body.innerHTML = '';
});

describe('StatementPage when the statement cannot load', () => {
  it('carries the support reference and offers a retry', async () => {
    server.use(
      http.get('/api/statements/:ownerId', () =>
        HttpResponse.json(
          { detail: 'The statement period is still closing.', correlationId: REFERENCE },
          { status: 503 },
        ),
      ),
    );
    renderStatement();

    expect(await screen.findByText("Couldn't load the statement")).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent('The statement period is still closing.');

    // An owner statement is the NCREC-facing artefact; "please retry in a moment" gave the operator
    // nothing to quote when it was actually the period lock talking.
    expect(screen.getByText(`Reference: ${REFERENCE}`)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeEnabled();
  });

  it('keeps the plain empty state for a missing owner id, which has no read to retry', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const router = createMemoryRouter([{ path: '/owners', element: <StatementPage /> }], {
      initialEntries: ['/owners'],
    });
    render(
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router} />
      </QueryClientProvider>,
    );

    expect(screen.getByText('Invalid statement URL')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
  });
});
