import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '@/test/mocks/server';
import { RunPreviewIssuedStatementNotice } from './RunPreviewIssuedStatementNotice';

const HARBORVIEW = {
  targetId: 'lease-1',
  ownerId: 'owner-1',
  ownerName: 'Harborview Holdings',
  basis: 'accrual',
  propertyId: null,
  propertyAddress: null,
  issuedYear: 2026,
  issuedMonth: 5,
};

const BEACON_SCOPED = {
  targetId: 'lease-2',
  ownerId: 'owner-2',
  ownerName: 'Beacon Ridge LLC',
  basis: 'cash',
  propertyId: 'property-9',
  propertyAddress: '72 Beacon Hill Ct',
  issuedYear: 2026,
  issuedMonth: 12,
};

function renderNotice(selectedTargetIds: ReadonlySet<string>) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const view = render(
    <QueryClientProvider client={queryClient}>
      <RunPreviewIssuedStatementNotice
        type="rent"
        year={2026}
        month={5}
        selectedTargetIds={selectedTargetIds}
      />
    </QueryClientProvider>,
  );

  return {
    ...view,
    rerenderWith(selected: ReadonlySet<string>) {
      view.rerender(
        <QueryClientProvider client={queryClient}>
          <RunPreviewIssuedStatementNotice
            type="rent"
            year={2026}
            month={5}
            selectedTargetIds={selected}
          />
        </QueryClientProvider>,
      );
    },
  };
}

describe('RunPreviewIssuedStatementNotice', () => {
  it('uses future-tense wording and filters the loaded answer as targets are selected', async () => {
    let requests = 0;
    server.use(
      http.get('/api/operations/runs/rent/preview/issued-coverage', () => {
        requests += 1;
        return HttpResponse.json({ rows: [HARBORVIEW, BEACON_SCOPED] });
      }),
    );

    const view = renderNotice(new Set(['lease-1']));
    const status = screen.getByRole('status');

    await vi.waitFor(() =>
      expect(status).toHaveTextContent(
        "If confirmed: Harborview Holdings — the accrual statement for May 2026 was already issued; this run's postings will appear as prior-period adjustments on their June 2026 accrual statement.",
      ),
    );
    expect(status).not.toHaveTextContent('Beacon Ridge LLC');

    view.rerenderWith(new Set(['lease-2']));

    expect(status).toHaveTextContent(
      "If confirmed: Beacon Ridge LLC — the cash statement for December 2026 (72 Beacon Hill Ct) was already issued; this run's postings will appear as prior-period adjustments on their January 2027 cash statement.",
    );
    expect(status).not.toHaveTextContent('Harborview Holdings');
    expect(requests).toBe(1);

    view.rerenderWith(new Set());
    expect(status).toBeEmptyDOMElement();
    expect(requests).toBe(1);
  });

  it('summarizes several selected owners with collapsible statement details', async () => {
    server.use(
      http.get('/api/operations/runs/rent/preview/issued-coverage', () =>
        HttpResponse.json({ rows: [HARBORVIEW, BEACON_SCOPED] }),
      ),
    );

    renderNotice(new Set(['lease-1', 'lease-2']));
    const status = screen.getByRole('status');

    expect(
      await within(status).findByText(/statements already issued for 2 owners/i),
    ).toBeVisible();
    await userEvent.click(within(status).getByText('Show affected statements'));
    expect(within(status).getAllByRole('listitem')).toHaveLength(2);
  });

  it('shows the specified non-blocking note when coverage cannot be read', async () => {
    server.use(
      http.get('/api/operations/runs/rent/preview/issued-coverage', () =>
        HttpResponse.json({ code: 'run_plan_failed' }, { status: 500 }),
      ),
    );

    const view = renderNotice(new Set(['lease-1']));

    expect(
      await screen.findByText("Couldn't check for issued statements affected by this run."),
    ).toBeInTheDocument();

    view.rerenderWith(new Set());
    expect(screen.getByRole('status')).toBeEmptyDOMElement();

    view.rerenderWith(new Set(['lease-1']));
    expect(
      await screen.findByText("Couldn't check for issued statements affected by this run."),
    ).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Dismiss' }));
    expect(screen.getByRole('status')).toBeEmptyDOMElement();
  });
});
