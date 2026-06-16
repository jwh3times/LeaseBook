import type { ReactNode } from 'react';

export interface CardProps {
  children: ReactNode;
  /** Apply the standard density-aware inner padding. */
  pad?: boolean;
  className?: string;
}

export function Card({ children, pad = false, className }: CardProps) {
  return (
    <div className={`pf-card${pad ? ' pf-card-pad' : ''}${className ? ` ${className}` : ''}`}>
      {children}
    </div>
  );
}

export interface CardHeaderProps {
  title: ReactNode;
  sub?: ReactNode;
  actions?: ReactNode;
}

export function CardHeader({ title, sub, actions }: CardHeaderProps) {
  return (
    <div className="pf-card-hd">
      <div className="col">
        <h3>{title}</h3>
        {sub && <span className="sub">{sub}</span>}
      </div>
      {actions && <div className="row gap8">{actions}</div>}
    </div>
  );
}
