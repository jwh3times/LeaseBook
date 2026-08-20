// API layer barrel (§C.2). Runtime configuration stays hand-authored; every endpoint function and
// contract type is generated from the host OpenAPI document by Hey API.
//
// This module owns everything about talking to the host: transport policy (baseUrl, credentials,
// XSRF), the success rule, file downloads, and the error vocabulary. `apiError.ts` lives here rather
// than in `lib/` because the split is what let `lib/telemetry.ts` re-implement transport policy
// without anything saying otherwise (ADR-025).
export { primeCsrf } from './client';
export { asApiError, toApiError, type ApiError } from './apiError';
export { download, unwrap, type ApiResult } from './request';
export * from './generated';
