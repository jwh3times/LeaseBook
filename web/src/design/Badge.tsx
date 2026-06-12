import type { ReactNode } from 'react';
import { Icon, type IconName } from './Icon';

export type BadgeTone = 'neutral' | 'pos' | 'neg' | 'warn' | 'accent';

export interface BadgeProps {
  children: ReactNode;
  tone?: BadgeTone;
  soft?: boolean;
  /** Status dot. Status is never color-alone (CLAUDE.md): the label — and optionally a dot/icon — carries it. */
  dot?: boolean;
  icon?: IconName;
}

export function Badge({ children, tone = 'neutral', soft = true, dot = false, icon }: BadgeProps) {
  return (
    <span className={`pf-badge tone-${tone}${soft ? ' soft' : ''}`}>
      {dot && <span className="pf-badge-dot" />}
      {icon && <Icon name={icon} size={12} />}
      {children}
    </span>
  );
}
