import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Money, MoneyDisplayProvider } from './Money';

describe('Money', () => {
  it('colorizes negatives with the neg class and still shows the sign (never color alone)', () => {
    const { container } = render(<Money value={-420} colorize />);
    const element = container.querySelector('.pf-money');
    expect(element).toHaveClass('neg');
    expect(element).toHaveTextContent('−$420.00');
  });

  it('renders an em-dash for zero', () => {
    render(<Money value={0} />);
    expect(screen.getByText('—')).toBeInTheDocument();
  });

  it('respects the parentheses display context', () => {
    render(
      <MoneyDisplayProvider negativeStyle="parens">
        <Money value={-8200} />
      </MoneyDisplayProvider>,
    );
    expect(screen.getByText('($8,200.00)')).toBeInTheDocument();
  });
});
