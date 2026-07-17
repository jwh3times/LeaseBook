import { fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { Modal } from './Modal';

function Harness() {
  const [open, setOpen] = useState(false);
  return (
    <div>
      <button onClick={() => setOpen(true)}>Open trigger</button>
      {open && (
        <Modal title="Test modal" onClose={() => setOpen(false)}>
          <input aria-label="First field" />
        </Modal>
      )}
    </div>
  );
}

describe('Modal', () => {
  it('moves focus to the first field inside the panel on open', () => {
    render(
      <Modal title="Test modal" onClose={() => {}}>
        <input aria-label="First field" />
      </Modal>,
    );
    expect(screen.getByLabelText('First field')).toHaveFocus();
  });

  it('restores focus to the trigger that opened it once the modal unmounts', () => {
    render(<Harness />);
    const trigger = screen.getByRole('button', { name: 'Open trigger' });
    trigger.focus();
    expect(trigger).toHaveFocus();

    // Opening the modal — its mount effect captures `trigger` (the active element) and
    // moves focus to the first field in the panel.
    fireEvent.click(trigger);
    expect(screen.getByLabelText('First field')).toHaveFocus();

    // Closing it unmounts the Modal — cleanup should restore focus to the trigger.
    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    expect(trigger).toHaveFocus();
  });

  it('calls onClose when Escape is pressed', () => {
    const onClose = vi.fn();
    render(
      <Modal title="Test modal" onClose={onClose}>
        <input aria-label="First field" />
      </Modal>,
    );
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
