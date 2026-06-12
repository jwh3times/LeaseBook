import { ThemeProvider } from '@/design';
import { KitchenSink } from '@/dev/KitchenSink';

// WP-07 shows the design system here; WP-08 replaces this with the routed application shell.
export function App() {
  return (
    <ThemeProvider>
      <KitchenSink />
    </ThemeProvider>
  );
}
