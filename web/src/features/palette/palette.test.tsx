import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { trackInteraction } from '@/lib/telemetry';
import { useGlobalShortcuts } from '@/lib/useGlobalShortcuts';
import { server } from '@/test/mocks/server';
import { CommandPalette } from './CommandPalette';
import { HelpOverlay } from './HelpOverlay';

// Only the budget sample is stubbed; `spentInteractions` stays real so the navigation-state
// assertions below exercise the value the app actually pushes.
vi.mock('@/lib/telemetry', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/telemetry')>()),
  trackInteraction: vi.fn(),
}));

function searchHandler(results: unknown[]) {
  return http.get('/api/search', () => HttpResponse.json(results));
}

// Where a chosen row actually navigated, including the history state it carried — the state is the
// only place the palette's already-spent interactions travel (#408), so a test has to read it there.
function LocationProbe() {
  const location = useLocation();
  return (
    <div data-testid="location">{`${location.pathname}${location.search} ${JSON.stringify(
      location.state ?? null,
    )}`}</div>
  );
}

function renderPalette(onClose = vi.fn()) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/start']}>
        <CommandPalette onClose={onClose} />
        <LocationProbe />
        <Routes>
          <Route path="/start" element={<div>start</div>} />
          <Route path="/tenants/:id" element={<div>tenant detail page</div>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return onClose;
}

const CARTER = { type: 'tenant', id: 't1', label: 'Jasmine Carter', sublabel: '#2B', score: 0.9 };
const HARGROVE = {
  type: 'owner',
  id: 'o1',
  label: 'Hargrove Family Trust',
  sublabel: '2 properties',
  score: 0.5,
};

function optionLabels(): (string | undefined)[] {
  return screen
    .getAllByRole('option')
    .map((option) => option.querySelector('.label')?.textContent ?? undefined);
}

describe('CommandPalette', () => {
  beforeEach(() => {
    localStorage.removeItem('leasebook.palette.recent');
    vi.clearAllMocks();
  });

  it('reports a failed search instead of claiming there are no matches', async () => {
    server.use(
      http.get('/api/search', () =>
        HttpResponse.json({ detail: 'Search temporarily unavailable.' }, { status: 503 }),
      ),
    );
    renderPalette();
    await userEvent.type(screen.getByLabelText('Search'), 'carter');
    expect(await screen.findByRole('alert')).toHaveTextContent('Search temporarily unavailable.');
    expect(screen.queryByText(/no matches/i)).not.toBeInTheDocument();
    server.use(searchHandler([]));
    await userEvent.clear(screen.getByLabelText('Search'));
    await userEvent.type(screen.getByLabelText('Search'), 'another');
    expect(await screen.findByText(/no matches for “another”/i)).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('queries on type, groups results, and jumps on Enter', async () => {
    server.use(searchHandler([CARTER, HARGROVE]));
    renderPalette();
    await userEvent.type(screen.getByLabelText('Search'), 'carter');
    expect(await screen.findByText('Jasmine Carter')).toBeInTheDocument();
    // The best match leads under its own header; the rest keep the per-type group headers (#408).
    expect(screen.getByText('Top result')).toBeInTheDocument();
    expect(screen.getByText('Owners')).toBeInTheDocument();
    await userEvent.keyboard('{Enter}');
    expect(await screen.findByText('tenant detail page')).toBeInTheDocument();
  });

  it('closes on Escape', async () => {
    server.use(searchHandler([]));
    const onClose = renderPalette();
    await userEvent.keyboard('{Escape}');
    expect(onClose).toHaveBeenCalled();
  });

  it('shows an empty message when nothing matches', async () => {
    server.use(searchHandler([]));
    renderPalette();
    await userEvent.type(screen.getByLabelText('Search'), 'zzz');
    expect(await screen.findByText(/no matches/i)).toBeInTheDocument();
  });

  it('opens with the top result, its other actions, then the remaining results (#408)', async () => {
    server.use(searchHandler([CARTER, HARGROVE]));
    renderPalette();
    await userEvent.type(screen.getByLabelText('Search'), 'car');
    await screen.findByText('Jasmine Carter');

    expect(optionLabels()).toEqual([
      'Jasmine Carter',
      'Record payment → Jasmine Carter',
      'Hargrove Family Trust',
    ]);
    expect(screen.getByText('Top result')).toBeInTheDocument();
    expect(screen.getByText('Actions')).toBeInTheDocument();
    expect(screen.getByText('Owners')).toBeInTheDocument();
    // Only the top result contributes actions: the owner's statement action stays out of the list.
    expect(screen.queryByText(/Owner statement/)).not.toBeInTheDocument();
    // Selection starts on the top result, so Enter still opens the entity as it always has.
    expect(screen.getAllByRole('option')[0]).toHaveAttribute('aria-selected', 'true');
  });

  it('opens the entity on Enter and carries no spent-interaction state', async () => {
    server.use(searchHandler([CARTER]));
    renderPalette();
    await userEvent.type(screen.getByLabelText('Search'), 'car');
    await screen.findByText('Jasmine Carter');
    await userEvent.keyboard('{Enter}');

    expect(await screen.findByText('tenant detail page')).toBeInTheDocument();
    // A plain jump is not a task launch: the ledger's own counter must still start from scratch.
    expect(screen.getByTestId('location')).toHaveTextContent('/tenants/t1 null');
  });

  it('runs a contextual action one arrow away, passing the interactions it already spent', async () => {
    server.use(searchHandler([CARTER]));
    renderPalette();
    await userEvent.type(screen.getByLabelText('Search'), 'car');
    await screen.findByText('Record payment → Jasmine Carter');
    await userEvent.keyboard('{ArrowDown}{Enter}');

    expect(screen.getByTestId('location')).toHaveTextContent(
      '/tenants/t1?compose=payment {"spentInteractions":2}',
    );
  });

  it('records an entity jump for a result row but not for an action row', async () => {
    server.use(searchHandler([CARTER]));
    renderPalette();
    await userEvent.type(screen.getByLabelText('Search'), 'car');
    await screen.findByText('Record payment → Jasmine Carter');
    await userEvent.keyboard('{ArrowDown}{Enter}');

    // An action launches a task, and the destination counts these same two interactions inside its
    // own budget. Sampling them as a jump as well would put one gesture in two budgets and pollute
    // the "reach any entity in ≤ 2" metric with rows from a different flow.
    expect(trackInteraction).not.toHaveBeenCalled();
  });

  it('records an entity jump when the top result itself is chosen', async () => {
    server.use(searchHandler([CARTER]));
    renderPalette();
    await userEvent.type(screen.getByLabelText('Search'), 'car');
    await screen.findByText('Jasmine Carter');
    await userEvent.keyboard('{Enter}');

    expect(trackInteraction).toHaveBeenCalledWith('entity-jump', 2, true);
  });

  it('pushes the entity to recents when an action is chosen, never the action', async () => {
    server.use(searchHandler([CARTER]));
    renderPalette();
    await userEvent.type(screen.getByLabelText('Search'), 'car');
    await screen.findByText('Record payment → Jasmine Carter');
    await userEvent.keyboard('{ArrowDown}{Enter}');

    expect(JSON.parse(localStorage.getItem('leasebook.palette.recent') ?? '[]')).toEqual([CARTER]);
  });

  it('shows recents without actions when the query is empty', async () => {
    localStorage.setItem('leasebook.palette.recent', JSON.stringify([CARTER]));
    renderPalette();

    expect(await screen.findByText('Recent')).toBeInTheDocument();
    expect(optionLabels()).toEqual(['Jasmine Carter']);
    expect(screen.queryByText('Actions')).not.toBeInTheDocument();
  });
});

function ShortcutHarness(props: {
  onPalette: () => void;
  onHelp: () => void;
  onNavigate: (p: string) => void;
}) {
  useGlobalShortcuts(props);
  return <input aria-label="field" />;
}

describe('global shortcuts', () => {
  it('opens the palette on ⌘K, opens help on ?, and jumps with g-prefix', async () => {
    const onPalette = vi.fn();
    const onHelp = vi.fn();
    const onNavigate = vi.fn();
    render(<ShortcutHarness onPalette={onPalette} onHelp={onHelp} onNavigate={onNavigate} />);

    await userEvent.keyboard('{Meta>}k{/Meta}');
    expect(onPalette).toHaveBeenCalled();

    await userEvent.keyboard('?');
    expect(onHelp).toHaveBeenCalled();

    await userEvent.keyboard('gt');
    expect(onNavigate).toHaveBeenCalledWith('/tenants');
  });

  it('ignores g-prefix and ? while typing in an input, but ⌘K still works', async () => {
    const onPalette = vi.fn();
    const onHelp = vi.fn();
    const onNavigate = vi.fn();
    render(<ShortcutHarness onPalette={onPalette} onHelp={onHelp} onNavigate={onNavigate} />);

    const field = screen.getByLabelText('field');
    field.focus();
    await userEvent.keyboard('?gt');
    expect(onHelp).not.toHaveBeenCalled();
    expect(onNavigate).not.toHaveBeenCalled();

    await userEvent.keyboard('{Meta>}k{/Meta}');
    expect(onPalette).toHaveBeenCalled();
  });
});

describe('HelpOverlay', () => {
  it('lists the shortcut map', () => {
    render(<HelpOverlay onClose={() => {}} />);
    expect(screen.getByText('Open command palette')).toBeInTheDocument();
    expect(screen.getByText('Go to Tenants')).toBeInTheDocument();
  });

  beforeEach(() => {
    document.body.innerHTML = '';
  });
});
