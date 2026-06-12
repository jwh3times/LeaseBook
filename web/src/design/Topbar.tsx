import type { ReactNode } from 'react';
import { IconButton } from './IconButton';
import { SearchBox } from './SearchBox';

export interface TopbarProps {
  title: string;
  onToggleSidebar?: () => void;
  /** Right-side actions (e.g. a "New" button, notifications, avatar). */
  actions?: ReactNode;
}

export function Topbar({ title, onToggleSidebar, actions }: TopbarProps) {
  return (
    <header className="pf-topbar">
      <div className="pf-top-left">
        <IconButton name="chevronLeft" label="Toggle sidebar" onClick={onToggleSidebar} />
        <h1 className="pf-top-title">{title}</h1>
      </div>

      {/* Search affordance with a ⌘K hint — non-functional placeholder; the command palette is M2. */}
      <SearchBox placeholder="Search owners, tenants, units, transactions…" kbd="⌘K" disabled />

      <div className="pf-top-right">{actions}</div>
    </header>
  );
}
