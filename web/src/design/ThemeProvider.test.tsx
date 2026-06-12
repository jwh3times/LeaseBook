import { fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';
import { ThemeProvider, useTheme } from './ThemeProvider';

function Probe() {
  const { theme, setTheme, setAccent } = useTheme();
  return (
    <div>
      <span data-testid="theme">{theme}</span>
      <button onClick={() => setTheme('dark')}>go dark</button>
      <button onClick={() => setAccent('violet')}>go violet</button>
    </div>
  );
}

describe('ThemeProvider', () => {
  beforeEach(() => {
    localStorage.clear();
    const root = document.documentElement;
    delete root.dataset.theme;
    delete root.dataset.accent;
    delete root.dataset.density;
  });

  it('applies theme/accent/density attributes to <html>', () => {
    render(
      <ThemeProvider>
        <Probe />
      </ThemeProvider>,
    );
    expect(document.documentElement.dataset.theme).toBe('light');
    expect(document.documentElement.dataset.accent).toBe('teal');
    expect(document.documentElement.dataset.density).toBe('comfortable');
  });

  it('updates the html attribute and persists when the theme changes', () => {
    render(
      <ThemeProvider>
        <Probe />
      </ThemeProvider>,
    );
    fireEvent.click(screen.getByRole('button', { name: 'go dark' }));
    expect(document.documentElement.dataset.theme).toBe('dark');
    expect(localStorage.getItem('leasebook.theme')).toContain('dark');
  });
});
