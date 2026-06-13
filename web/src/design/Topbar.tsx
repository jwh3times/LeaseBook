import type { ReactNode } from 'react';
import { IconButton } from './IconButton';
import { SearchBox } from './SearchBox';

export interface TopbarProps {
  title: string;
  onToggleSidebar?: () => void;
  /** Opens the ⌘K command palette — when set, the search affordance becomes a live trigger (M2). */
  onSearchClick?: () => void;
  /** Right-side actions (e.g. a "New" button, notifications, avatar). */
  actions?: ReactNode;
}

export function Topbar({ title, onToggleSidebar, onSearchClick, actions }: TopbarProps) {
  return (
    <header className="pf-topbar">
      <div className="pf-top-left">
        <IconButton name="chevronLeft" label="Toggle sidebar" onClick={onToggleSidebar} />
        <h1 className="pf-top-title">{title}</h1>
      </div>

      {/* The search affordance opens the command palette (⌘K). Read-only — typing happens in the palette. */}
      <SearchBox
        placeholder="Search owners, tenants, units, banks…"
        kbd="⌘K"
        readOnly
        disabled={!onSearchClick}
        aria-label="Open command palette"
        onClick={onSearchClick}
        onFocus={onSearchClick}
      />

      <div className="pf-top-right">{actions}</div>
    </header>
  );
}
