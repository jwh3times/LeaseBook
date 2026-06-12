import type { ReactNode, SelectHTMLAttributes } from 'react';

export interface SelectProps extends SelectHTMLAttributes<HTMLSelectElement> {
  children: ReactNode;
}

export function Select({ className, children, ...rest }: SelectProps) {
  return (
    <select className={`pf-field${className ? ` ${className}` : ''}`} {...rest}>
      {children}
    </select>
  );
}
