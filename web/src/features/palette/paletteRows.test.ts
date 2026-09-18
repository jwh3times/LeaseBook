import { describe, expect, it } from 'vitest';
import type { SearchResult } from '@/lib/search';
import { recentRows, searchRows } from './paletteRows';

const result = (type: string, id: string, label: string, score = 1): SearchResult => ({
  type,
  id,
  label,
  sublabel: null,
  score,
  propertyId: null,
});

const tenant = result('tenant', 't1', 'Jasmine Carter', 0.9);
const owner = result('owner', 'o1', 'Pat Owner', 0.4);
const property = result('property', 'p1', '12 Oak St', 0.3);
const bank = result('bank', 'b1', 'Operating Trust', 0.2);

describe('searchRows', () => {
  it('opens with the top result, then that result’s non-primary actions', () => {
    const rows = searchRows([tenant, owner]);

    expect(rows[0]).toMatchObject({ kind: 'result', header: 'Top result', result: tenant });
    expect(rows[1]).toMatchObject({
      kind: 'action',
      header: 'Actions',
      result: tenant,
      action: { id: 'record-payment' },
    });
  });

  it('never offers the primary action as its own row — the top result already runs it', () => {
    const rows = searchRows([tenant]);

    expect(rows.filter((row) => row.kind === 'action').map((row) => row.action.id)).toEqual([
      'record-payment',
    ]);
  });

  it('shows no Actions group for a type with a single action', () => {
    const rows = searchRows([property, owner]);

    expect(rows.some((row) => row.header === 'Actions')).toBe(false);
    expect(rows.some((row) => row.kind === 'action')).toBe(false);
  });

  it('groups the remaining results by type and never repeats the top result', () => {
    const rows = searchRows([bank, owner, tenant, property]);

    expect(rows.map((row) => row.header)).toEqual([
      'Top result',
      'Owners',
      'Properties',
      'Tenants',
    ]);
    // Grouped by type in the fixed type order, not the score order they arrived in.
    expect(rows.map((row) => row.result.id)).toEqual(['b1', 'o1', 'p1', 't1']);
    expect(rows.filter((row) => row.result.id === bank.id)).toHaveLength(1);
  });

  it('takes the top result in score order, before the type grouping reorders anything', () => {
    // The server returns score DESC; a bank sorts last by type but is still the best match here.
    const rows = searchRows([bank, owner]);

    expect(rows[0]).toMatchObject({ header: 'Top result', result: bank });
  });

  it('is empty for no results', () => {
    expect(searchRows([])).toEqual([]);
  });
});

describe('recentRows', () => {
  it('lists recents under one header and offers no actions', () => {
    const rows = recentRows([tenant, owner]);

    expect(rows.map((row) => row.header)).toEqual(['Recent', null]);
    expect(rows.every((row) => row.kind === 'result')).toBe(true);
  });

  it('is empty for no recents', () => {
    expect(recentRows([])).toEqual([]);
  });
});
