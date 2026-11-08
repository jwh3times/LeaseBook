import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ReactQueryDevtools } from '@tanstack/react-query-devtools';
import type { ReactNode } from 'react';
import { ThemeProvider } from '@/design';
import { RecordNavProvider } from '@/components/recordNav';
import { queryDefaults } from './queryDefaults';

const queryClient = new QueryClient({ defaultOptions: queryDefaults });

export function Providers({ children }: { children: ReactNode }) {
  return (
    <QueryClientProvider client={queryClient}>
      <ThemeProvider>
        <RecordNavProvider>
          {children}
          {import.meta.env.DEV && <ReactQueryDevtools initialIsOpen={false} />}
        </RecordNavProvider>
      </ThemeProvider>
    </QueryClientProvider>
  );
}
