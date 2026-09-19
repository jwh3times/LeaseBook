import { readdirSync, readFileSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { describe, expect, it } from 'vitest';

const SRC = join(import.meta.dirname, '..');
const SKIP = /node_modules|generated|dist/;

function sourceFiles(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) {
      if (!SKIP.test(full)) sourceFiles(full, out);
      // Test files ship no styles, and this one names example tokens in its own prose.
    } else if (/\.(css|tsx|ts)$/.test(entry.name) && !/\.test\.tsx?$/.test(entry.name)) {
      out.push(full);
    }
  }
  return out;
}

/**
 * #416: a `var(--token)` naming a custom property nothing defines is not a CSS error. The
 * declaration is simply dropped, so the element falls back to `inherit` or `initial` — the command
 * palette and every modal rendered fully transparent for months because two rules asked for
 * `--surface-1` and the tokens are `--surface`/`--surface-2`/`--surface-3`. Nothing type-checks a
 * token name and nothing renders red, so this scan is the only thing that can see it.
 *
 * A declared fallback (`var(--x, red)`) does not excuse an undefined name: it hides the typo behind
 * a value that never tracks the theme, which is how a hardcoded hex survived in two rules here.
 */
describe('design tokens', () => {
  it('defines every custom property that the SPA references', () => {
    const defined = new Set<string>();
    const referenced = new Map<string, Set<string>>();

    for (const file of sourceFiles(SRC)) {
      const text = readFileSync(file, 'utf8');
      for (const [, name] of text.matchAll(/(--[A-Za-z0-9_-]+)\s*:/g)) defined.add(name!);
      for (const [, name] of text.matchAll(/var\(\s*(--[A-Za-z0-9_-]+)\s*[,)]/g)) {
        const where = referenced.get(name!) ?? new Set<string>();
        where.add(relative(SRC, file).split(sep).join('/'));
        referenced.set(name!, where);
      }
    }

    const undefinedRefs = [...referenced]
      .filter(([name]) => !defined.has(name))
      .map(([name, where]) => `${name} (referenced in ${[...where].join(', ')})`)
      .sort();

    expect(undefinedRefs, 'add the token to design/tokens.css, or fix the name').toEqual([]);
  });

  it('finds the token file it is meant to be scanning', () => {
    // Guards the scan itself: a moved tokens.css or a broken walk would empty `defined` and make the
    // assertion above vacuous rather than red.
    const text = readFileSync(join(SRC, 'design', 'tokens.css'), 'utf8');
    expect(text).toMatch(/--surface\s*:/);
    expect(text).toMatch(/--text\s*:/);
  });
});
