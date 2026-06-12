import { setupServer } from 'msw/node';
import { handlers } from './handlers';

// The mock API server used across the web test suite. Handlers are reset between tests.
export const server = setupServer(...handlers);
