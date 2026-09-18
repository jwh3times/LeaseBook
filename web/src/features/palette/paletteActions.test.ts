import { describe, expect, it } from 'vitest';
import type { SearchResult } from '@/lib/search';
import { actionsFor, primaryRoute } from './paletteActions';

const result = (
  type: string,
  id = 'x1',
  label = 'Label',
  propertyId: string | null = null,
): SearchResult => ({
  type,
  id,
  label,
  sublabel: null,
  score: 1,
  propertyId,
});

describe('actionsFor', () => {
  it('offers an owner their page first, then their statement page', () => {
    expect(actionsFor(result('owner', 'o1', 'Pat Owner'))).toEqual([
      { id: 'open-owner', label: 'Open Pat Owner', route: '/owners/o1' },
      {
        id: 'owner-statement',
        label: 'Owner statement · Pat Owner',
        route: '/owners/o1/statement',
      },
    ]);
  });

  it('offers a tenant their ledger first, then recording a payment on it', () => {
    expect(actionsFor(result('tenant', 't1', 'Jasmine Carter'))).toEqual([
      { id: 'open-ledger', label: 'Open ledger · Jasmine Carter', route: '/tenants/t1' },
      {
        id: 'record-payment',
        label: 'Record payment → Jasmine Carter',
        route: '/tenants/t1?compose=payment',
      },
    ]);
  });

  it('opens a bank account in its register, not in settings', () => {
    expect(actionsFor(result('bank', 'b1', 'Operating Trust'))).toEqual([
      { id: 'open-bank', label: 'Open Operating Trust', route: '/banking?account=b1' },
    ]);
  });

  it('opens a unit on its owning property, not the properties list', () => {
    expect(actionsFor(result('unit', 'u1', '#2B', 'p9'))).toEqual([
      { id: 'open-unit', label: 'Open #2B', route: '/properties/p9' },
    ]);
  });

  // The server fills propertyId for every unit (#409), so this is the shape of a contract that has
  // drifted, not an ordinary state. The list is the only honest destination left — better than a
  // route with "null" in it, which 404s and tells the operator nothing.
  it('falls back to the properties list for a unit with no owning property', () => {
    expect(primaryRoute(result('unit', 'u1', '#2B'))).toBe('/properties');
  });

  it('opens a property on its detail page', () => {
    expect(primaryRoute(result('property', 'p1'))).toBe('/properties/p1');
  });

  it('is pure: repeated calls return equal, independent lists', () => {
    const first = actionsFor(result('tenant'));
    first.pop();
    expect(actionsFor(result('tenant'))).toHaveLength(2);
  });

  it('falls back to the dashboard for a type it does not know', () => {
    expect(actionsFor(result('invoice'))).toEqual([]);
    expect(primaryRoute(result('invoice'))).toBe('/dashboard');
  });
});
