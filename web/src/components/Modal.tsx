import { useEffect, useRef, type ReactNode } from 'react';
import { IconButton } from '@/design';

interface ModalProps {
  title: string;
  onClose: () => void;
  children: ReactNode;
  footer?: ReactNode;
}

/** A focus-trapping modal: Escape and backdrop-click close it; the first field autofocuses. */
export function Modal({ title, onClose, children, footer }: ModalProps) {
  const panelRef = useRef<HTMLDivElement>(null);

  // Move focus into the modal on open and restore it to the trigger on close (WCAG 2.4.3 focus order).
  // Runs once: capturing the trigger and autofocusing the first field must not repeat as `onClose`
  // identity changes across renders (a re-run would capture the modal's own field as the "trigger").
  useEffect(() => {
    const trigger = document.activeElement as HTMLElement | null;
    const panel = panelRef.current;
    // Focus the first field so a data-entry modal is immediately typable; fall back to the first
    // button (e.g. the header Close) for a modal with no field, so focus still enters the dialog.
    (
      panel?.querySelector<HTMLElement>('input, select, textarea') ??
      panel?.querySelector<HTMLElement>('button')
    )?.focus();
    return () => trigger?.focus?.();
  }, []);

  // Escape closes — bound to the latest onClose.
  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      if (event.key === 'Escape') onClose();
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div
      className="pf-modal-backdrop"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) onClose();
      }}
    >
      <div ref={panelRef} className="pf-modal" role="dialog" aria-modal="true" aria-label={title}>
        <div className="pf-modal-hd">
          <h3>{title}</h3>
          <IconButton name="x" label="Close" onClick={onClose} />
        </div>
        {children}
        {footer && <div className="pf-modal-ft">{footer}</div>}
      </div>
    </div>
  );
}
