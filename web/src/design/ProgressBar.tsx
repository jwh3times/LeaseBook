export interface ProgressBarProps {
  /** 0–100; clamped to 100. */
  pct: number;
  tone?: 'accent' | 'pos';
  label?: string;
}

export function ProgressBar({ pct, tone = 'accent', label }: ProgressBarProps) {
  const clamped = Math.max(0, Math.min(100, pct));
  return (
    <div
      className="pf-prog"
      role="progressbar"
      aria-label={label}
      aria-valuenow={Math.round(clamped)}
      aria-valuemin={0}
      aria-valuemax={100}
    >
      <div className={`pf-prog-fill tone-${tone}`} style={{ width: `${clamped}%` }} />
    </div>
  );
}
