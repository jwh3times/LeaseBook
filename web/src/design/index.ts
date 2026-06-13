// Design system barrel (WP-07). Shared UI lives only here (§C.2). Importing the barrel also pulls
// in the design tokens so any consumer gets the themed styles.
import './tokens.css';
import './m2.css';
import './ledger.css';

export { ThemeProvider, useTheme } from './ThemeProvider';
export type { Theme, Accent, Density, ThemeState } from './ThemeProvider';

export { formatMoney, formatMoneyPlain, formatMoneyK } from './formatMoney';
export type { NegativeStyle, FormatMoneyOptions } from './formatMoney';

export { Icon, ICONS } from './Icon';
export type { IconName, IconProps } from './Icon';

export { Avatar } from './Avatar';
export type { AvatarProps } from './Avatar';

export { Badge } from './Badge';
export type { BadgeProps, BadgeTone } from './Badge';

export { Money, MoneyDisplayProvider } from './Money';
export type { MoneyProps } from './Money';

export { Button } from './Button';
export type { ButtonProps, ButtonVariant, ButtonSize } from './Button';

export { IconButton } from './IconButton';
export type { IconButtonProps } from './IconButton';

export { ProgressBar } from './ProgressBar';
export type { ProgressBarProps } from './ProgressBar';

export { Sparkline } from './Sparkline';
export type { SparklineProps } from './Sparkline';

export { Card, CardHeader } from './Card';
export type { CardProps, CardHeaderProps } from './Card';

export { Table } from './Table';
export type { TableColumn, TableProps } from './Table';

export { Input } from './Input';
export type { InputProps } from './Input';

export { Select } from './Select';
export type { SelectProps } from './Select';

export { SearchBox } from './SearchBox';
export type { SearchBoxProps } from './SearchBox';

export { StatCard } from './StatCard';
export type { StatCardProps } from './StatCard';

export { FilterChip } from './FilterChip';
export type { FilterChipProps } from './FilterChip';

export { EmptyState } from './EmptyState';
export type { EmptyStateProps } from './EmptyState';

export { Sidebar } from './Sidebar';
export type { SidebarProps, NavItem, SidebarUser } from './Sidebar';

export { Topbar } from './Topbar';
export type { TopbarProps } from './Topbar';

export { AppLayout } from './AppLayout';
export type { AppLayoutProps } from './AppLayout';
