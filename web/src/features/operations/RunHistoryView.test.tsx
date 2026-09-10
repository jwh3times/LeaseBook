import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { RunHistoryView } from './RunHistoryView';

const RUNS = {
  runs: [
    {
      id: 'r1',
      runType: 'rent',
      periodYear: 2026,
      periodMonth: 2,
      summaryJson: JSON.stringify({ posted: 12, skipped: 0, excluded: 0, total: 17400 }),
      createdAt: '2026-02-01T09:00:00Z',
    },
  ],
};

const REFERENCE = 'b1b2b3b4b1b2b3b4b1b2b3b4b1b2b3b4';

function renderHistory() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <RunHistoryView />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  document.body.innerHTML = '';
});

describe('RunHistoryView when the history cannot load', () => {
  it('carries the support reference and offers a retry', async () => {
    server.use(
      http.get('/api/operations/runs', () =>
        HttpResponse.json(
          { detail: 'The run log is rebuilding.', correlationId: REFERENCE },
          { status: 503 },
        ),
      ),
    );
    renderHistory();

    expect(await screen.findByText("Couldn't load run history")).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent('The run log is rebuilding.');
    expect(screen.getByText(`Reference: ${REFERENCE}`)).toBeInTheDocument();

    // A bulk run that posted money and a bulk run that was never read are different facts.
    expect(screen.queryByText('No runs yet')).toBeNull();
  });

  it('recovers when the retry succeeds', async () => {
    let attempt = 0;
    server.use(
      http.get('/api/operations/runs', () => {
        attempt += 1;
        return attempt === 1 ? new HttpResponse(null, { status: 500 }) : HttpResponse.json(RUNS);
      }),
    );
    renderHistory();

    await userEvent.click(await screen.findByRole('button', { name: 'Retry' }));

    expect(await screen.findByText('February 2026')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('still reports a genuine empty history as an empty state', async () => {
    server.use(http.get('/api/operations/runs', () => HttpResponse.json({ runs: [] })));
    renderHistory();

    expect(await screen.findByText('No runs yet')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
