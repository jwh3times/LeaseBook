import type { IconName } from '@/design';
import type { SearchResult } from '@/lib/search';

/** A thing the palette can do with a matched entity. `route` is where selecting it navigates. */
export interface PaletteAction {
  id: string;
  label: string;
  route: string;
}

/**
 * Everything the palette can do with a matched entity, primary action first (§C.7). A pure, exhaustive
 * mapping — no registration and no module state (#207): one source of actions exists, and a second,
 * genuinely independent one is the point at which an extension seam would earn its keep.
 */
export function actionsFor(result: SearchResult): PaletteAction[] {
  switch (result.type) {
    case 'owner':
      return [
        { id: 'open-owner', label: `Open ${result.label}`, route: `/owners/${result.id}` },
        {
          id: 'owner-statement',
          label: `Owner statement · ${result.label}`,
          route: `/owners/${result.id}/statement`,
        },
      ];
    case 'property':
      return [
        { id: 'open-property', label: `Open ${result.label}`, route: `/properties/${result.id}` },
      ];
    case 'tenant':
      // "Record payment" routes into the ledger with the M3 compose flag, which opens the inline
      // composer in payment mode.
      return [
        {
          id: 'open-ledger',
          label: `Open ledger · ${result.label}`,
          route: `/tenants/${result.id}`,
        },
        {
          id: 'record-payment',
          label: `Record payment → ${result.label}`,
          route: `/tenants/${result.id}?compose=payment`,
        },
      ];
    case 'unit':
      return [{ id: 'open-unit', label: `Open ${result.label}`, route: '/properties' }];
    case 'bank':
      return [
        {
          id: 'open-bank',
          label: `Open ${result.label}`,
          route: `/banking?account=${encodeURIComponent(result.id)}`,
        },
      ];
    default:
      return [];
  }
}

/** The default action a palette result runs on Enter (its first action). */
export function primaryRoute(result: SearchResult): string {
  return actionsFor(result)[0]?.route ?? '/dashboard';
}

export function iconForType(type: SearchResult['type']): IconName {
  switch (type) {
    case 'owner':
      return 'owners';
    case 'property':
      return 'building';
    case 'tenant':
      return 'tenants';
    case 'unit':
      return 'building';
    case 'bank':
      return 'bank';
    default:
      return 'search';
  }
}

export function groupLabel(type: SearchResult['type']): string {
  return (
    { owner: 'Owners', property: 'Properties', unit: 'Units', tenant: 'Tenants', bank: 'Banks' }[
      type
    ] ?? type
  );
}
