import type { InputHTMLAttributes } from 'react';

export type InputProps = InputHTMLAttributes<HTMLInputElement>;

export function Input({ className, type = 'text', ...rest }: InputProps) {
  return <input type={type} className={`pf-field${className ? ` ${className}` : ''}`} {...rest} />;
}
