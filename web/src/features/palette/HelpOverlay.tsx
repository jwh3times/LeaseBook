import { Modal } from '@/components/Modal';

const SHORTCUTS: { keys: string; desc: string }[] = [
  { keys: '⌘K', desc: 'Open command palette' },
  { keys: '?', desc: 'Show keyboard shortcuts' },
  { keys: 'g d', desc: 'Go to Dashboard' },
  { keys: 'g t', desc: 'Go to Tenants' },
  { keys: 'g o', desc: 'Go to Owners' },
  { keys: 'g p', desc: 'Go to Properties' },
  { keys: 'g b', desc: 'Go to Banking' },
  { keys: '↑ ↓', desc: 'Move selection in a list' },
  { keys: 'Enter', desc: 'Open the selected record' },
  { keys: '[ ]', desc: 'Previous / next record' },
  { keys: 'Esc', desc: 'Close a dialog or the palette' },
];

/** The `?` help overlay listing the full keyboard map (§C.7 / Report §4.3). */
export function HelpOverlay({ onClose }: { onClose: () => void }) {
  return (
    <Modal title="Keyboard shortcuts" onClose={onClose}>
      <div className="pf-modal-body">
        <div className="pf-help-grid">
          {SHORTCUTS.map((shortcut) => (
            <div key={shortcut.keys} className="pf-help-row">
              <span>{shortcut.desc}</span>
              <kbd className="pf-kbd">{shortcut.keys}</kbd>
            </div>
          ))}
        </div>
      </div>
    </Modal>
  );
}
