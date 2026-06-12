import { createContext, useContext, type ReactNode } from 'react';
import { formatMoney, formatMoneyPlain, type NegativeStyle } from './formatMoney';

// Display preference for how negatives render, defaulting to a minus sign. A future settings screen
// can wrap the app in this provider to switch the whole UI to accounting parentheses.
const NegativeStyleContext = createContext<NegativeStyle>('minus');

export function MoneyDisplayProvider({
  negativeStyle,
  children,
}: {
  negativeStyle: NegativeStyle;
  children: ReactNode;
}) {
  return <NegativeStyleContext.Provider value={negativeStyle}>{children}</NegativeStyleContext.Provider>;
}

export interface MoneyProps {
  value: number;
  /** Larger display weight (KPI headline). */
  big?: boolean;
  /** Tint positive green / negative red (in addition to the sign — never color alone). */
  colorize?: boolean;
  /** Magnitude only ("$X.XX"), no sign or zero-dash. */
  plain?: boolean;
  sign?: boolean;
  negativeStyle?: NegativeStyle;
}

export function Money({ value, big = false, colorize = false, plain = false, sign = false, negativeStyle }: MoneyProps) {
  const contextStyle = useContext(NegativeStyleContext);
  const classes = ['pf-money'];
  if (big) classes.push('big');
  if (colorize) classes.push(value < 0 ? 'neg' : value > 0 ? 'pos' : 'zero');

  const text = plain
    ? formatMoneyPlain(value)
    : formatMoney(value, { sign, negativeStyle: negativeStyle ?? contextStyle });

  return <span className={classes.join(' ')}>{text}</span>;
}
