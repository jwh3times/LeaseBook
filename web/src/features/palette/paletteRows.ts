import type { SearchResult } from '@/lib/search';
import { actionsFor, groupLabel, type PaletteAction } from './paletteActions';

/**
 * One selectable line in the palette listbox — an entity, or one of the top result's contextual
 * actions. `header` is the non-selectable group label rendered above the line, `null` when the line
 * continues the group above it. Every row is an ARIA `option`; headers never are.
 */
export type PaletteRow = {
  key: string;
  header: string | null;
  /** The entity the row acts on. An action row's Recent entry is this, never the action (#408). */
  result: SearchResult;
} & ({ kind: 'result' } | { kind: 'action'; action: PaletteAction });

const TYPE_ORDER: SearchResult['type'][] = ['owner', 'property', 'unit', 'tenant', 'bank'];

function byType(results: SearchResult[]): SearchResult[] {
  return [...results].sort((a, b) => TYPE_ORDER.indexOf(a.type) - TYPE_ORDER.indexOf(b.type));
}

/**
 * The searched layout (#408): the highest-scoring result under **Top result**, its *non-primary*
 * actions under **Actions**, then the remaining results grouped by type. `results` arrives in the
 * server's `score DESC` order, so the top result is taken before the type grouping reorders anything,
 * and it is not repeated below. The primary action gets no row of its own — activating the top result
 * already runs it, which is what keeps Enter on the initial selection opening the entity.
 */
export function searchRows(results: SearchResult[]): PaletteRow[] {
  const [top, ...rest] = results;
  if (!top) return [];

  const rows: PaletteRow[] = [
    { kind: 'result', key: `top-${top.type}-${top.id}`, header: 'Top result', result: top },
  ];

  for (const [index, action] of actionsFor(top).slice(1).entries()) {
    rows.push({
      kind: 'action',
      key: `action-${action.id}`,
      header: index === 0 ? 'Actions' : null,
      result: top,
      action,
    });
  }

  const grouped = byType(rest);
  for (const [index, result] of grouped.entries()) {
    rows.push({
      kind: 'result',
      key: `${result.type}-${result.id}`,
      header:
        index === 0 || grouped[index - 1]!.type !== result.type ? groupLabel(result.type) : null,
      result,
    });
  }

  return rows;
}

/** The empty-query view: entities jumped to before, under one header and with no actions (#408). */
export function recentRows(results: SearchResult[]): PaletteRow[] {
  return results.map((result, index) => ({
    kind: 'result',
    key: `${result.type}-${result.id}`,
    header: index === 0 ? 'Recent' : null,
    result,
  }));
}
