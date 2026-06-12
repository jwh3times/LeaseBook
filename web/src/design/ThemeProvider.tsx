import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';

export type Theme = 'light' | 'dark';
export type Accent = 'teal' | 'violet' | 'green' | 'navy';
export type Density = 'comfortable' | 'balanced' | 'compact';

export interface ThemeState {
  theme: Theme;
  accent: Accent;
  density: Density;
  setTheme: (theme: Theme) => void;
  setAccent: (accent: Accent) => void;
  setDensity: (density: Density) => void;
}

const STORAGE_KEY = 'leasebook.theme';
const ThemeContext = createContext<ThemeState | null>(null);

interface StoredPrefs {
  theme?: Theme;
  accent?: Accent;
  density?: Density;
}

function readStored(): StoredPrefs {
  if (typeof localStorage === 'undefined') return {};
  try {
    return JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '{}') as StoredPrefs;
  } catch {
    return {};
  }
}

function prefersDark(): boolean {
  // matchMedia is absent under jsdom (tests); fall back to light.
  return typeof window !== 'undefined' && typeof window.matchMedia === 'function'
    ? window.matchMedia('(prefers-color-scheme: dark)').matches
    : false;
}

export function ThemeProvider({ children }: { children: ReactNode }) {
  const stored = readStored();
  const [theme, setTheme] = useState<Theme>(stored.theme ?? (prefersDark() ? 'dark' : 'light'));
  const [accent, setAccent] = useState<Accent>(stored.accent ?? 'teal');
  const [density, setDensity] = useState<Density>(stored.density ?? 'comfortable');

  useEffect(() => {
    const root = document.documentElement;
    root.dataset.theme = theme;
    root.dataset.accent = accent;
    root.dataset.density = density;
    root.dataset.font = 'jakarta';
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify({ theme, accent, density }));
    } catch {
      // Persistence is best-effort; the in-memory state is the source of truth this session.
    }
  }, [theme, accent, density]);

  return (
    <ThemeContext.Provider value={{ theme, accent, density, setTheme, setAccent, setDensity }}>
      {children}
    </ThemeContext.Provider>
  );
}

// eslint-disable-next-line react-refresh/only-export-components -- hook colocated with its provider
export function useTheme(): ThemeState {
  const context = useContext(ThemeContext);
  if (!context) {
    throw new Error('useTheme must be used within a ThemeProvider.');
  }
  return context;
}
