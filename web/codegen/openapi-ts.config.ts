import { defineConfig } from '@hey-api/openapi-ts';

export default defineConfig({
  input: '../../src/LeaseBook.Web/obj/openapi/LeaseBook.Web.json',
  output: '../src/api/generated',
  plugins: [
    '@hey-api/typescript',
    {
      name: '@hey-api/client-fetch',
      runtimeConfigPath: '../src/api/runtime.ts',
    },
    {
      name: '@hey-api/sdk',
      responseStyle: 'fields',
    },
  ],
});
