import { Fragment, type KeyboardEvent, useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Icon } from '@/design';
import { useSearch, type SearchResult } from '@/lib/search';
import { trackInteraction } from '@/lib/telemetry';
import { groupLabel, iconForType, primaryRoute } from './actionRegistry';
import { getRecent, pushRecent } from './recent';

const TYPE_ORDER: SearchResult['type'][] = ['owner', 'property', 'unit', 'tenant', 'bank'];

function groupByType(results: SearchResult[]): SearchResult[] {
  return [...results].sort((a, b) => TYPE_ORDER.indexOf(a.type) - TYPE_ORDER.indexOf(b.type));
}

/**
 * The ⌘K command palette (§C.5/§C.7): debounced cross-entity search grouped by type, recent items when
 * empty, full keyboard operation (↑/↓ move · Enter jumps · Esc closes), focus trap, and ARIA roles.
 * Selecting an entity runs its primary registered action (navigation) and records the jump.
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
  const items = useMemo(
    () => (showRecent ? recent : groupByType(search.data ?? [])),
    [showRecent, recent, search.data],
  );

  useEffect(() => setSelected(0), [debounced, items.length]);

  function activate(result: SearchResult) {
    pushRecent(result);
    trackInteraction('entity-jump', 2, true); // open palette + select ≤ 2 interactions to reach any entity
    navigate(primaryRoute(result));
    onClose();
  }

  function onKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      setSelected((index) => Math.min(index + 1, items.length - 1));
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      setSelected((index) => Math.max(index - 1, 0));
    } else if (event.key === 'Enter') {
      const result = items[selected];
      if (result) {
        event.preventDefault();
        activate(result);
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
          />
          <kbd className="pf-kbd">esc</kbd>
        </div>

        <div className="pf-palette-list" id="palette-list" role="listbox">
          {showRecent && recent.length > 0 && <div className="pf-palette-group">Recent</div>}
          {showRecent && recent.length === 0 && <div className="pf-palette-empty">Type to search across the directory.</div>}
          {!showRecent && search.isFetching && items.length === 0 && <div className="pf-palette-empty">Searching…</div>}
          {!showRecent && !search.isFetching && items.length === 0 && (
            <div className="pf-palette-empty">No matches for “{debounced}”.</div>
          )}

          {items.map((result, index) => {
            const showHeader = !showRecent && (index === 0 || items[index - 1]!.type !== result.type);
            return (
              <Fragment key={`${result.type}-${result.id}`}>
                {showHeader && <div className="pf-palette-group">{groupLabel(result.type)}</div>}
                <div
                  className={`pf-palette-item${index === selected ? ' sel' : ''}`}
                  role="option"
                  aria-selected={index === selected}
                  onMouseEnter={() => setSelected(index)}
                  onClick={() => activate(result)}
                >
                  <Icon name={iconForType(result.type)} size={16} />
                  <span className="label">{result.label}</span>
                  {result.sublabel && <span className="sub">{result.sublabel}</span>}
                </div>
              </Fragment>
            );
          })}
        </div>
      </div>
    </div>
  );
}
