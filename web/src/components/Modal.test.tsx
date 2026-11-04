import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
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

// #561: a dialog given `onSubmit` is one form, footer included, so its submit button is the form's
// default button. Enter in a field then submits it, and the fields' own constraints run first.
describe('Modal as a form', () => {
  function FormModal({ onSubmit }: { onSubmit: () => void }) {
    return (
      <Modal
        title="Form modal"
        onClose={() => {}}
        onSubmit={(event) => {
          event.preventDefault();
          onSubmit();
        }}
        footer={
          <>
            <button type="button">Cancel</button>
            <button type="submit">Save</button>
          </>
        }
      >
        <input aria-label="Name" required />
        <input aria-label="Email" type="email" />
      </Modal>
    );
  }

  it('submits once when Enter is pressed in a field', async () => {
    const onSubmit = vi.fn();
    render(<FormModal onSubmit={onSubmit} />);
    await userEvent.type(screen.getByLabelText('Name'), 'Ada');
    await userEvent.type(screen.getByLabelText('Email'), 'ada@example.test{Enter}');
    expect(onSubmit).toHaveBeenCalledTimes(1);
  });

  it('submits once from the footer button', async () => {
    const onSubmit = vi.fn();
    render(<FormModal onSubmit={onSubmit} />);
    await userEvent.type(screen.getByLabelText('Name'), 'Ada');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(onSubmit).toHaveBeenCalledTimes(1);
  });

  it('does not submit while a field fails its own constraint', async () => {
    const onSubmit = vi.fn();
    render(<FormModal onSubmit={onSubmit} />);
    await userEvent.type(screen.getByLabelText('Name'), 'Ada');
    await userEvent.type(screen.getByLabelText('Email'), 'not-an-email{Enter}');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(onSubmit).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Email')).toBeInvalid();
  });
});

// #554: Tab never leaves an open dialog for the page behind it, as aria-modal promises.
describe('Modal focus trap', () => {
  function renderWithFooter() {
    render(
      <>
        <button type="button">Behind the dialog</button>
        <Modal
          title="Test modal"
          onClose={() => {}}
          footer={
            <>
              <button type="button">Cancel</button>
              <button type="button">Confirm</button>
            </>
          }
        >
          <input aria-label="First field" />
        </Modal>
      </>,
    );
  }

  it('wraps Tab from the last control back to the first', async () => {
    renderWithFooter();
    expect(screen.getByLabelText('First field')).toHaveFocus();

    await userEvent.tab(); // Cancel
    await userEvent.tab(); // Confirm
    await userEvent.tab();

    expect(screen.getByRole('button', { name: 'Close' })).toHaveFocus();
  });

  it('wraps Shift+Tab from the first control to the last', async () => {
    renderWithFooter();
    screen.getByRole('button', { name: 'Close' }).focus();

    await userEvent.tab({ shift: true });

    expect(screen.getByRole('button', { name: 'Confirm' })).toHaveFocus();
  });

  it('brings focus that left the panel back into it', async () => {
    renderWithFooter();
    // A click on the panel's text, or on the backdrop's edge, leaves focus on the body.
    (document.activeElement as HTMLElement).blur();

    await userEvent.tab();

    expect(screen.getByRole('button', { name: 'Close' })).toHaveFocus();
  });
});
