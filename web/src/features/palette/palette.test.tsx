import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useGlobalShortcuts } from '@/lib/useGlobalShortcuts';
import { server } from '@/test/mocks/server';
import { CommandPalette } from './CommandPalette';
import { HelpOverlay } from './HelpOverlay';

function searchHandler(results: unknown[]) {
  return http.get('/api/search', () => HttpResponse.json(results));
}

function renderPalette(onClose = vi.fn()) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/start']}>
        <CommandPalette onClose={onClose} />
        <Routes>
          <Route path="/start" element={<div>start</div>} />
          <Route path="/tenants/:id" element={<div>tenant detail page</div>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return onClose;
}

describe('CommandPalette', () => {
  it('queries on type, groups results, and jumps on Enter', async () => {
    server.use(
      searchHandler([
        { type: 'tenant', id: 't1', label: 'Jasmine Carter', sublabel: '#2B', score: 0.9 },
      ]),
    );
    renderPalette();
    await userEvent.type(screen.getByLabelText('Search'), 'carter');
    expect(await screen.findByText('Jasmine Carter')).toBeInTheDocument();
    expect(screen.getByText('Tenants')).toBeInTheDocument(); // group header
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
