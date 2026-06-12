import type { ReactNode } from 'react';
import { Icon, type IconName } from './Icon';

export interface EmptyStateProps {
  icon?: IconName;
  title: string;
  description?: ReactNode;
  action?: ReactNode;
}

// Not in the prototype — a simple shared primitive used by every list/section that can be empty.
export function EmptyState({ icon = 'info', title, description, action }: EmptyStateProps) {
  return (
    <div className="pf-empty">
      <div className="pf-empty-icon">
        <Icon name={icon} size={22} />
      </div>
      <h4>{title}</h4>
      {description && <p>{description}</p>}
      {action}
    </div>
  );
}
