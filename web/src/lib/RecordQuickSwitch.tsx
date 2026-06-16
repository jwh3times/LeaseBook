import { useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { IconButton } from '@/design';
import { isTypingTarget } from './keyboard';
import { useRecordNav, type EntityKind } from './recordNav';

/**
 * Prev/Next through the list the user came from (§C.7), so entering payments down a list never returns
 * to the index. Buttons + the `[` / `]` keys move within the current filtered order.
 */
export function RecordQuickSwitch({
  kind,
  currentId,
  toPath,
}: {
  kind: EntityKind;
  currentId: string;
  toPath: (id: string) => string;
}) {
  const navigate = useNavigate();
  const { prev, next } = useRecordNav(kind, currentId);

  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      if (isTypingTarget(event.target)) return;
      if (event.key === '[' && prev) {
        event.preventDefault();
        navigate(toPath(prev));
      } else if (event.key === ']' && next) {
        event.preventDefault();
        navigate(toPath(next));
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [prev, next, navigate, toPath]);

  return (
    <div className="pf-switcher" role="group" aria-label="Record navigation">
      <IconButton
        name="chevronLeft"
        label="Previous record ([)"
        disabled={!prev}
        onClick={() => prev && navigate(toPath(prev))}
      />
      <IconButton
        name="chevronRight"
        label="Next record (])"
        disabled={!next}
        onClick={() => next && navigate(toPath(next))}
      />
    </div>
  );
}
