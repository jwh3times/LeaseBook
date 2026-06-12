import { render } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Badge } from './Badge';

describe('Badge', () => {
  it('applies the tone class and renders a dot when requested', () => {
    const { container } = render(
      <Badge tone="neg" dot>
        Late
      </Badge>,
    );
    const badge = container.querySelector('.pf-badge');
    expect(badge).toHaveClass('tone-neg');
    expect(badge?.querySelector('.pf-badge-dot')).not.toBeNull();
  });

  it('omits the dot by default — the label carries the status', () => {
    const { container } = render(<Badge tone="pos">Current</Badge>);
    expect(container.querySelector('.pf-badge-dot')).toBeNull();
  });
});
