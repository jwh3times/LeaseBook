import type { IconName } from '@/design';
import type { SearchResult } from '@/lib/search';

/** A thing the palette can do with a matched entity. `route` is where selecting it navigates. */
export interface PaletteAction {
  id: string;
  label: string;
  route: string;
}

/** Contributes actions for a matched entity. Later modules register their own without touching the palette. */
export type ActionProvider = (result: SearchResult) => PaletteAction[];

const providers: ActionProvider[] = [];

export function registerActionProvider(provider: ActionProvider): void {
  providers.push(provider);
}

export function actionsFor(result: SearchResult): PaletteAction[] {
  return providers.flatMap((provider) => provider(result));
}

/** The default action a palette result runs on Enter (its first registered action). */
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
  return { owner: 'Owners', property: 'Properties', unit: 'Units', tenant: 'Tenants', bank: 'Banks' }[type] ?? type;
}

// M2 navigation actions (§C.7). "Record payment → tenant" wires the seam but routes to the tenant detail
// — the inline ledger composer is M3. Registered once at module load.
registerActionProvider((result) => {
  switch (result.type) {
    case 'owner':
      return [
        { id: 'open-owner', label: `Open ${result.label}`, route: `/owners/${result.id}` },
        { id: 'owner-statement', label: `Owner statement · ${result.label}`, route: `/owners/${result.id}` },
      ];
    case 'property':
      return [{ id: 'open-property', label: `Open ${result.label}`, route: `/properties/${result.id}` }];
    case 'tenant':
      return [
        { id: 'open-ledger', label: `Open ledger · ${result.label}`, route: `/tenants/${result.id}` },
        { id: 'record-payment', label: `Record payment → ${result.label}`, route: `/tenants/${result.id}` },
      ];
    case 'unit':
      return [{ id: 'open-unit', label: `Open ${result.label}`, route: '/properties' }];
    case 'bank':
      return [{ id: 'open-bank', label: `Open ${result.label}`, route: '/settings' }];
    default:
      return [];
  }
});
