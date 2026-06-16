import type { ReactNode } from 'react';
import { Icon, type IconName } from './Icon';

export interface FilterChipProps {
  children: ReactNode;
  active?: boolean;
  icon?: IconName;
  onClick?: () => void;
}

export function FilterChip({ children, active = false, icon, onClick }: FilterChipProps) {
  return (
    <button
      type="button"
      className={`pf-chip${active ? ' active' : ''}`}
      aria-pressed={active}
      onClick={onClick}
    >
      {icon && <Icon name={icon} size={14} />}
      {children}
    </button>
  );
}
