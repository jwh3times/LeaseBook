import type { ButtonHTMLAttributes } from 'react';
import { Icon, type IconName } from './Icon';

export type ButtonVariant = 'default' | 'primary' | 'ghost' | 'soft';
export type ButtonSize = 'sm' | 'md';

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: ButtonSize;
  icon?: IconName;
}

/**
 * The button class scheme, exported so the rare element that must wear the button shape without
 * being a `<button>` — a link that is genuinely a navigation — stays pinned to it. Without this the
 * literal string is copied, and renaming a variant silently unstyles the copy.
 */
export function buttonClassName(
  variant: ButtonVariant = 'default',
  size: ButtonSize = 'md',
  className?: string,
): string {
  return `pf-btn v-${variant} s-${size}${className ? ` ${className}` : ''}`;
}

export function Button({
  children,
  variant = 'default',
  size = 'md',
  icon,
  className,
  type = 'button',
  ...rest
}: ButtonProps) {
  return (
    <button type={type} className={buttonClassName(variant, size, className)} {...rest}>
      {icon && <Icon name={icon} size={size === 'sm' ? 15 : 16} />}
      {children}
    </button>
  );
}
