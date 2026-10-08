import { useState } from 'react';

/**
 * Form state for a card that edits part of a stored record other cards also edit (#511).
 *
 * The card holds only its unsaved edit. With no edit it shows the stored values as they are now, so
 * a save elsewhere that replaces the record cannot overwrite what the user has typed here, and a
 * card with nothing typed still follows the record. `discard` after the card's own save returns it
 * to the stored values. Copying the record into state and resetting it in an effect does neither.
 */
export function useDraft<T extends NonNullable<unknown>>(stored: T) {
  const [draft, setDraft] = useState<T | null>(null);
  return {
    value: draft ?? stored,
    edit: (change: (current: T) => T) => setDraft((current) => change(current ?? stored)),
    discard: () => setDraft(null),
  };
}
