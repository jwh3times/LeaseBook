import type { InputHTMLAttributes } from 'react';
import { Icon } from './Icon';

export interface SearchBoxProps extends InputHTMLAttributes<HTMLInputElement> {
  /** Keyboard hint shown on the right (e.g. "⌘K"). */
  kbd?: string;
}

export function SearchBox({ kbd, className, placeholder = 'Search…', ...rest }: SearchBoxProps) {
  return (
    <div className={`pf-search${className ? ` ${className}` : ''}`}>
      <Icon name="search" size={17} />
      <input placeholder={placeholder} {...rest} />
      {kbd && <kbd>{kbd}</kbd>}
    </div>
  );
}
