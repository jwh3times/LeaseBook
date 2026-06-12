import type { CSSProperties } from 'react';

// Functional UI glyphs ported from the prototype's icon set (components.jsx).
// eslint-disable-next-line react-refresh/only-export-components -- icon-path data colocated with Icon
export const ICONS = {
  dashboard: 'M3 3h7v8H3V3zm11 0h7v5h-7V3zM3 14h7v7H3v-7zm11-3h7v10h-7V11z',
  owners: 'M3 21V8l6-4 6 4v13M9 21v-5h2v5M15 21V11l3-2 3 2v10M3 21h18',
  tenants:
    'M9 11a4 4 0 100-8 4 4 0 000 8zm0 2c-3.5 0-6 2-6 4.5V21h12v-2.5c0-2.5-2.5-4.5-6-4.5zm9-1a3 3 0 100-6 3 3 0 000 6zm3 8v-1.5c0-1.8-1.5-3.2-3.5-3.5',
  bank: 'M3 21h18M4 10h16M5 10l7-6 7 6M6 10v8M10 10v8M14 10v8M18 10v8M4 21v-3h16v3',
  reports: 'M4 20V4M4 20h16M8 16v-4M12 16V8M16 16v-7M20 16v-2',
  search: 'M11 19a8 8 0 100-16 8 8 0 000 16zm10 2l-4.3-4.3',
  plus: 'M12 5v14M5 12h14',
  bell: 'M18 8a6 6 0 10-12 0c0 7-3 9-3 9h18s-3-2-3-9M13.7 21a2 2 0 01-3.4 0',
  chevronDown: 'M6 9l6 6 6-6',
  chevronRight: 'M9 6l6 6-6 6',
  chevronLeft: 'M15 6l-6 6 6 6',
  settings:
    'M12 15a3 3 0 100-6 3 3 0 000 6zM19.4 15a1.6 1.6 0 00.3 1.8l.1.1a2 2 0 11-2.8 2.8l-.1-.1a1.6 1.6 0 00-1.8-.3 1.6 1.6 0 00-1 1.5V21a2 2 0 11-4 0v-.1a1.6 1.6 0 00-1-1.5 1.6 1.6 0 00-1.8.3l-.1.1a2 2 0 11-2.8-2.8l.1-.1a1.6 1.6 0 00.3-1.8 1.6 1.6 0 00-1.5-1H3a2 2 0 110-4h.1a1.6 1.6 0 001.5-1 1.6 1.6 0 00-.3-1.8l-.1-.1a2 2 0 112.8-2.8l.1.1a1.6 1.6 0 001.8.3H9a1.6 1.6 0 001-1.5V3a2 2 0 114 0v.1a1.6 1.6 0 001 1.5 1.6 1.6 0 001.8-.3l.1-.1a2 2 0 112.8 2.8l-.1.1a1.6 1.6 0 00-.3 1.8V9a1.6 1.6 0 001.5 1H21a2 2 0 110 4h-.1a1.6 1.6 0 00-1.5 1z',
  download: 'M12 3v12m0 0l-4-4m4 4l4-4M4 17v2a2 2 0 002 2h12a2 2 0 002-2v-2',
  filter: 'M3 5h18M6 12h12M10 19h4',
  check: 'M5 13l4 4L19 7',
  alert: 'M12 9v4m0 4h.01M10.3 3.9L2 18a2 2 0 001.7 3h16.6a2 2 0 001.7-3L13.7 3.9a2 2 0 00-3.4 0z',
  info: 'M12 16v-4m0-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z',
  arrowUpRight: 'M7 17L17 7M7 7h10v10',
  x: 'M18 6L6 18M6 6l12 12',
  sun: 'M12 17a5 5 0 100-10 5 5 0 000 10zM12 1v2M12 21v2M4.2 4.2l1.4 1.4M18.4 18.4l1.4 1.4M1 12h2M21 12h2M4.2 19.8l1.4-1.4M18.4 5.6l1.4-1.4',
  doc: 'M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8l-6-6zM14 2v6h6M9 13h6M9 17h6',
  building:
    'M3 21V5a2 2 0 012-2h6a2 2 0 012 2v16M13 9h6a2 2 0 012 2v10M3 21h18M7 7h2M7 11h2M7 15h2M17 13h.01M17 17h.01',
  wallet: 'M19 7V5a2 2 0 00-2-2H5a2 2 0 00-2 2v14a2 2 0 002 2h14a2 2 0 002-2v-2M16 12h5v4h-5a2 2 0 010-4z',
  clock: 'M12 21a9 9 0 100-18 9 9 0 000 18zM12 7v5l3 2',
  refresh: 'M3 12a9 9 0 0115.5-6.4L21 8M21 3v5h-5M21 12a9 9 0 01-15.5 6.4L3 16M3 21v-5h5',
} as const;

export type IconName = keyof typeof ICONS;

export interface IconProps {
  name: IconName;
  size?: number;
  stroke?: number;
  style?: CSSProperties;
}

export function Icon({ name, size = 18, stroke = 1.75, style }: IconProps) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={stroke}
      strokeLinecap="round"
      strokeLinejoin="round"
      style={{ flex: 'none', ...style }}
      aria-hidden="true"
    >
      <path d={ICONS[name]} />
    </svg>
  );
}
