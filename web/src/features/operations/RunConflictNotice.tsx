import { InfoNotice } from '@/components/InfoNotice';
import type { RunMode } from './useRunFlow';

/**
 * Shown after a capability conflict re-previewed the run: the amounts the operator was approving may
 * have changed, and in a selective run their ticks were cleared — a selection that vanishes with no
 * reason given reads as a bug.
 *
 * The `role="status"` container is always mounted and the notice itself is not a live region:
 * screen readers reliably announce text added to an existing live region, and often miss one that
 * mounts already filled. It sits above the preview's own states so an empty re-preview still says
 * why.
 */
export function RunConflictNotice({ show, mode }: { show: boolean; mode: RunMode }) {
  return (
    <div role="status">
      {show && (
        <InfoNotice live={false} style={{ marginBottom: 12 }}>
          {mode === 'selective'
            ? 'Settings changed since this preview. The amounts were recalculated and your selection was cleared. Review and confirm again.'
            : 'Settings changed since this preview. The amounts were recalculated. Review and confirm again.'}
        </InfoNotice>
      )}
    </div>
  );
}
