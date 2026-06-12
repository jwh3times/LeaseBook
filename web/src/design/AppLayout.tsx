import type { ReactNode } from 'react';

export interface AppLayoutProps {
  sidebar: ReactNode;
  topbar: ReactNode;
  children: ReactNode;
  collapsed?: boolean;
}

// Grid shell + scroll region (the prototype's .pf-app / .pf-main / .pf-scroll structure).
export function AppLayout({ sidebar, topbar, children, collapsed = false }: AppLayoutProps) {
  return (
    <div className={`pf-app${collapsed ? ' collapsed' : ''}`}>
      {sidebar}
      <div className="pf-main">
        {topbar}
        <div className="pf-scroll">
          <div className="pf-page">{children}</div>
        </div>
      </div>
    </div>
  );
}
