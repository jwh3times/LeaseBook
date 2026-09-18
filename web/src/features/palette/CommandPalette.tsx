import { type KeyboardEvent, useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router';
import { Icon } from '@/design';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { useSearch } from '@/lib/search';
import { spentInteractions, trackInteraction } from '@/lib/telemetry';
import { iconForType, primaryRoute } from './paletteActions';
import { groupRows, type PaletteRow, recentRows, searchRows } from './paletteRows';
import { getRecent, pushRecent } from './recent';

/** ⌘K to open plus the pick — what reaching any row costs, and the budget for an entity jump. */
const PALETTE_INTERACTIONS = 2;

/** Positional, so it is always a valid id and always unique, whatever a result is labelled (#413). */
const optionId = (index: number) => `palette-option-${index}`;

/**
 * The ⌘K command palette (§C.5/§C.7): debounced cross-entity search, recent items when empty, full
 * keyboard operation (↑/↓ move · Enter jumps · Esc closes), focus trap, and ARIA roles. The list opens
 * with the best match and its contextual actions (#408) — "Record payment → X" is one ↓ away — then
 * the remaining matches grouped by type. Selecting a row navigates and records the jump.
 */
export function CommandPalette({ onClose }: { onClose: () => void }) {
  const navigate = useNavigate();
  const [q, setQ] = useState('');
  const [debounced, setDebounced] = useState('');
  const [selected, setSelected] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    const timer = setTimeout(() => setDebounced(q), 150);
    return () => clearTimeout(timer);
  }, [q]);
  useEffect(() => inputRef.current?.focus(), []);

  const search = useSearch(debounced);
  const recent = useMemo(() => getRecent(), []);
  const showRecent = debounced.trim().length === 0;
  const rows = useMemo(
    () => (showRecent ? recentRows(recent) : searchRows(search.data ?? [])),
    [showRecent, recent, search.data],
  );
  const groups = useMemo(() => groupRows(rows), [rows]);

  useEffect(() => setSelected(0), [debounced, rows.length]);

  // Focus never leaves the input, so the selected row is announced through `aria-activedescendant`
  // rather than by being focused (#413). With nothing to select the attribute is dropped entirely:
  // pointing at an option that is not in the DOM is worse than pointing at nothing.
  const activeId = rows[selected] ? optionId(selected) : undefined;

  // The same pattern requires the active option to be visible — an announced row the operator cannot
  // see is only half the fix, and a long result list scrolls.
  useEffect(() => {
    if (!activeId) return;
    document.getElementById(activeId)?.scrollIntoView({ block: 'nearest' });
  }, [activeId]);

  function activate(row: PaletteRow) {
    // Recents track where the operator went, so an action records its *entity* — "Record payment →
    // Jasmine Carter" is not a place to come back to; Jasmine Carter is (#408).
    pushRecent(row.result);
    if (row.kind === 'action') {
      // The palette has already spent ⌘K plus this pick. Handing the count to the destination keeps
      // its own budget sample honest — a bare palette-launched payment is 3 interactions, not 2.
      //
      // No `entity-jump` sample here: these two interactions are the opening of the destination's
      // task and get counted again inside its own budget. Recording them as a jump as well would put
      // one gesture in two budgets, and fill the "reach any entity in ≤ 2" metric with rows from a
      // flow that was never an entity jump.
      void navigate(row.action.route, { state: spentInteractions(PALETTE_INTERACTIONS) });
    } else {
      trackInteraction('entity-jump', PALETTE_INTERACTIONS, true);
      void navigate(primaryRoute(row.result));
    }
    onClose();
  }

  function onKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      setSelected((index) => Math.max(0, Math.min(index + 1, rows.length - 1)));
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      setSelected((index) => Math.max(index - 1, 0));
    } else if (event.key === 'Enter') {
      const row = rows[selected];
      if (row) {
        event.preventDefault();
        activate(row);
      }
    } else if (event.key === 'Escape') {
      event.preventDefault();
      onClose();
    } else if (event.key === 'Tab') {
      event.preventDefault(); // focus trap — the input is the only focusable control
    }
  }

  return (
    <div
      className="pf-palette-backdrop"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) onClose();
      }}
    >
      <div className="pf-palette" role="dialog" aria-modal="true" aria-label="Command palette">
        <div className="pf-palette-input">
          <Icon name="search" size={18} />
          <input
            ref={inputRef}
            value={q}
            onChange={(event) => setQ(event.target.value)}
            onKeyDown={onKeyDown}
            placeholder="Search owners, tenants, properties, banks…"
            aria-label="Search"
            role="combobox"
            aria-expanded
            aria-controls="palette-list"
            aria-activedescendant={activeId}
          />
          <kbd className="pf-kbd">esc</kbd>
        </div>

        {!showRecent && search.isError && (
          <div className="pf-palette-empty">
            <ApiErrorNotice error={search.error} kind="read" />
          </div>
        )}
        <div className="pf-palette-list" id="palette-list" role="listbox" aria-label="Results">
          {showRecent && rows.length === 0 && (
            <div className="pf-palette-empty">Type to search across the directory.</div>
          )}
          {!showRecent && search.isFetching && rows.length === 0 && (
            <div className="pf-palette-empty">Searching…</div>
          )}
          {!showRecent && search.isSuccess && !search.isFetching && rows.length === 0 && (
            <div className="pf-palette-empty">No matches for “{debounced}”.</div>
          )}

          {groups.map((group) => (
            <div
              key={group.header ?? `group-${group.items[0]!.row.key}`}
              role="group"
              aria-label={group.header ?? undefined}
            >
              {group.header && <div className="pf-palette-group">{group.header}</div>}
              {group.items.map(({ row, index }) => (
                <div
                  key={row.key}
                  id={optionId(index)}
                  className={`pf-palette-item${index === selected ? ' sel' : ''}`}
                  role="option"
                  aria-selected={index === selected}
                  onMouseEnter={() => setSelected(index)}
                  onClick={() => activate(row)}
                >
                  <Icon
                    name={row.kind === 'action' ? 'arrowUpRight' : iconForType(row.result.type)}
                    size={16}
                  />
                  <span className="label">
                    {row.kind === 'action' ? row.action.label : row.result.label}
                  </span>
                  {row.kind === 'result' && row.result.sublabel && (
                    <span className="sub">{row.result.sublabel}</span>
                  )}
                </div>
              ))}
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
