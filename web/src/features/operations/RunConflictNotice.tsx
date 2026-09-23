import { InfoNotice } from '@/components/InfoNotice';

/**
 * Shown after a capability conflict re-previewed the run: the operator's selection emptied because
 * the amounts they were approving may have changed, and a selection that vanishes with no reason
 * given reads as a bug.
 */
export function RunConflictNotice({ show }: { show: boolean }) {
  if (!show) return null;
  return (
    <InfoNotice style={{ marginTop: 8 }}>
      Settings changed since this preview. The amounts were recalculated and your selection was
      cleared. Review and confirm again.
    </InfoNotice>
  );
}
