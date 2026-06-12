export interface AvatarProps {
  initials: string;
  size?: number;
  /** Optional background override (defaults to the accent gradient from CSS). */
  tone?: string;
}

export function Avatar({ initials, size = 34, tone }: AvatarProps) {
  return (
    <div className="pf-avatar" style={{ width: size, height: size, fontSize: size * 0.38, background: tone }}>
      {initials}
    </div>
  );
}
