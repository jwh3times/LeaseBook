// API layer barrel (§C.2). Runtime configuration stays hand-authored; every endpoint function and
// contract type is generated from the host OpenAPI document by Hey API.
export { primeCsrf } from './client';
export * from './generated';
