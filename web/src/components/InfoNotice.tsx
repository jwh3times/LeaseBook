import type { CSSProperties, ReactNode } from 'react';
import { Icon, IconButton } from '@/design';

export interface InfoNoticeProps {
  children: ReactNode;
  /** Renders a dismiss button when given. The notice never requires dismissal to continue. */
  onDismiss?: () => void;
  /**
   * Whether this element is itself the live region. Pass `false` when a caller keeps its own
   * `role="status"` container mounted ahead of the content — screen readers reliably announce text
   * added to an existing live region, but often miss one that mounts already filled.
   */
  live?: boolean;
  className?: string;
  style?: CSSProperties;
}

/**
 * The non-blocking information counterpart of {@link ApiErrorNotice}: something the operator should
 * know about what just happened, which asks nothing of them. `role="status"` announces it politely
 * without moving focus, and the meaning is carried by the icon and the words — never colour alone.
 */
export function InfoNotice({
  children,
  onDismiss,
  live = true,
  className,
  style,
}: InfoNoticeProps) {
  return (
    <div
      className={`pf-info-notice${className ? ` ${className}` : ''}`}
      role={live ? 'status' : undefined}
      style={style}
    >
      <Icon name="info" size={16} aria-hidden="true" />
      <div className="pf-info-notice-body">{children}</div>
      {onDismiss && <IconButton name="x" label="Dismiss" onClick={onDismiss} />}
    </div>
  );
}
