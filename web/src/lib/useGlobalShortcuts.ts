import { useEffect, useRef } from 'react';
import { isTypingTarget } from './keyboard';

export interface ShortcutHandlers {
  onPalette: () => void;
  onHelp: () => void;
  onNavigate: (path: string) => void;
}

// `g`-prefixed go-to shortcuts (Report §4.3). Press `g` then the destination key.
const GO_TO: Record<string, string> = {
  d: '/dashboard',
  t: '/tenants',
  o: '/owners',
  p: '/properties',
  b: '/banking',
};

/**
 * Global keyboard shortcuts (§C.7): ⌘K / Ctrl+K opens the palette, `?` opens help, and a `g`-prefix
 * jumps to a section (`g d`, `g t`, …). All are ignored while a text input is focused so typing is never
 * hijacked. The `g` prefix expires after a short window.
 */
export function useGlobalShortcuts({ onPalette, onHelp, onNavigate }: ShortcutHandlers): void {
  const pendingG = useRef<number | null>(null);

  useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      // ⌘K / Ctrl+K always opens the palette, even from an input.
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault();
        onPalette();
        return;
      }

      if (isTypingTarget(event.target) || event.metaKey || event.ctrlKey || event.altKey) {
        return;
      }

      const now = Date.now();

      if (pendingG.current && now - pendingG.current < 1200) {
        pendingG.current = null;
        const destination = GO_TO[event.key.toLowerCase()];
        if (destination) {
          event.preventDefault();
          onNavigate(destination);
        }
        return;
      }

      if (event.key === 'g') {
        pendingG.current = now;
        return;
      }

      if (event.key === '?') {
        event.preventDefault();
        onHelp();
      }
    }

    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [onPalette, onHelp, onNavigate]);
}
