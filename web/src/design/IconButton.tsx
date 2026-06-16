import type { ButtonHTMLAttributes } from 'react';
import { Icon, type IconName } from './Icon';

export interface IconButtonProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'children'> {
  name: IconName;
  /** Accessible label — required (the button has no text). */
  label: string;
  active?: boolean;
  size?: number;
}

export function IconButton({
  name,
  label,
  active = false,
  size = 18,
  className,
  type = 'button',
  ...rest
}: IconButtonProps) {
  return (
    <button
      type={type}
      className={`pf-iconbtn${active ? ' active' : ''}${className ? ` ${className}` : ''}`}
      aria-label={label}
      title={label}
      {...rest}
    >
      <Icon name={name} size={size} />
    </button>
  );
}
