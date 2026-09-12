import { defineConfig } from '@hey-api/openapi-ts';

export default defineConfig({
  input: '../../src/LeaseBook.Web/obj/openapi/LeaseBook.Web.json',
  output: '../src/api/generated',
  plugins: [
    '@hey-api/typescript',
    {
      name: '@hey-api/client-fetch',
      runtimeConfigPath: '../src/api/runtime.ts',
      // No baked default base URL. `web/src/api/runtime.ts` sets `baseUrl: window.location.origin`
      // on every client, so a generated default is dead config — and when the generator is handed a
      // URL input it infers one from that origin, which is how `npm run api:generate` used to emit a
      // client hardcoding `http://localhost:5080/` that the CI drift gate then rejected (#369).
      // `false` removes the origin divergence. It does not make a URL input equivalent to this one —
      // declaration order still differs — which is why there is now a single input rather than two.
      baseUrl: false,
    },
    {
      name: '@hey-api/sdk',
      responseStyle: 'fields',
    },
  ],
});
