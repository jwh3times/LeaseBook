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

  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      if (event.key === 'Escape') onClose();
    }
    window.addEventListener('keydown', onKey);
    panelRef.current?.querySelector<HTMLElement>('input, select, textarea, button')?.focus();
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
