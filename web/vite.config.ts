import { fileURLToPath, URL } from 'node:url';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// https://vite.dev — dev server on 5373 (offset from the default), /api proxied to the
// .NET host on 5080. The 5373 port lets the SPA coexist with other local projects' Vite servers.
// LEASEBOOK_API_PROXY_TARGET points the proxy at another host: only the Stripe sandbox run's
// Playwright pass sets it (`playwright.config.ts`), for the host `scripts/stripe-sandbox-run.mjs`
// starts on a port of its own.
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    port: 5373,
    proxy: {
      '/api': {
        target: process.env.LEASEBOOK_API_PROXY_TARGET || 'http://localhost:5080',
        changeOrigin: true,
      },
    },
  },
  test: {
    globals: true,
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.{test,spec}.{ts,tsx}'],
    css: false,
    // Measurement only (ADR-055): collected by `npm run test:coverage`, never by `npm run test`,
    // and no threshold is set, so a figure cannot fail a run. `include` names the whole source tree
    // so a file no test imports counts as uncovered instead of going missing from the report.
    coverage: {
      provider: 'v8',
      include: ['src/**/*.{ts,tsx}'],
      exclude: [
        'src/api/generated/**',
        'src/test/**',
        'src/**/*.{test,spec}.{ts,tsx}',
        'src/**/*.d.ts',
      ],
      reporter: ['text-summary', 'html', 'cobertura', 'json-summary'],
      reportsDirectory: './coverage/unit',
    },
  },
});
