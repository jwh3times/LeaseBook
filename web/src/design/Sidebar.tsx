import { Avatar } from './Avatar';
import { Icon, type IconName } from './Icon';

export interface NavItem {
  id: string;
  label: string;
  icon: IconName;
}

export interface SidebarUser {
  name: string;
  role: string;
  initials: string;
}

export interface SidebarProps {
  /** Company name shown under the LeaseBook brand. */
  brand: string;
  items: NavItem[];
  activeId: string;
  onNavigate: (id: string) => void;
  user: SidebarUser;
  collapsed?: boolean;
  onSettings?: () => void;
}

export function Sidebar({ brand, items, activeId, onNavigate, user, collapsed = false, onSettings }: SidebarProps) {
  return (
    <aside className={`pf-sidebar${collapsed ? ' collapsed' : ''}`}>
      <div className="pf-brand">
        <div className="pf-logo">
          <span />
        </div>
        {!collapsed && (
          <div className="pf-brand-txt">
            <b>LeaseBook</b>
            <span>{brand}</span>
          </div>
        )}
      </div>

      <nav className="pf-nav">
        {items.map((item) => {
          const active = activeId === item.id;
          return (
            <button
              key={item.id}
              type="button"
              className={`pf-navitem${active ? ' active' : ''}`}
              onClick={() => onNavigate(item.id)}
              title={collapsed ? item.label : undefined}
              aria-current={active ? 'page' : undefined}
            >
              <Icon name={item.icon} size={19} />
              {!collapsed && <span>{item.label}</span>}
              {active && <span className="pf-navmark" />}
            </button>
          );
        })}
      </nav>

      <div className="pf-side-foot">
        <button type="button" className="pf-navitem" title="Settings" onClick={onSettings}>
          <Icon name="settings" size={19} />
          {!collapsed && <span>Settings</span>}
        </button>
        <div className="pf-user">
          <Avatar initials={user.initials} size={collapsed ? 30 : 34} tone="var(--accent)" />
          {!collapsed && (
            <div className="pf-user-txt">
              <b>{user.name}</b>
              <span>{user.role}</span>
            </div>
          )}
        </div>
      </div>
    </aside>
  );
}
