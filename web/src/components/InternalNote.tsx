import { Icon } from '@/design';

/**
 * Who reads a free-text field (#468, ADR-047). Every staff-typed posting has two: the description,
 * which prints on the owner statement, and an internal note, which never leaves staff surfaces. The
 * hint says so in words beside the field — never by colour alone — and the caller points the input's
 * `aria-describedby` at `id`, so a screen reader announces the audience with the label.
 */
export function AudienceHint({ id, audience }: { id: string; audience: 'owner' | 'staff' }) {
  return (
    <span id={id} className={`pf-audience-hint ${audience}`}>
      <Icon name={audience === 'owner' ? 'eye' : 'lock'} size={12} />
      {audience === 'owner' ? 'Owner sees this' : 'Staff only'}
    </span>
  );
}

export interface InternalNoteTextProps {
  note: string;
  /**
   * The words that mark the note as staff-only. Staff tables say "Internal note"; the staff owner
   * statement, which otherwise looks like the owner's copy, says so outright.
   */
  label?: string;
}

/**
 * A staff-only internal note rendered as a secondary line under a description. The lock glyph and
 * the leading label carry the meaning; the muted tone is decoration on top of them.
 */
export function InternalNoteText({ note, label = 'Internal note' }: InternalNoteTextProps) {
  return (
    <span className="pf-internal-note" title={`${label}: ${note}`}>
      <Icon name="lock" size={11} style={{ verticalAlign: '-1px', marginRight: 4 }} />
      <span className="pf-internal-note-label">{label}:</span>{' '}
      <span className="pf-internal-note-text">{note}</span>
    </span>
  );
}
