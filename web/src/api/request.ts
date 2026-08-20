import { toApiError } from './apiError';

/**
 * What every generated SDK function resolves to. Declared here rather than imported from the
 * generated tree so the helpers below do not depend on Hey API's internal result generics.
 */
export interface ApiResult<T> {
  data?: T;
  error?: unknown;
  response?: Response;
}

/**
 * Runs a generated SDK call and returns its body, or throws an {@link ApiError} carrying the
 * server's `code` and `correlationId` (ADR-025).
 *
 * This replaces the hand-written `if (error || !data) throw new Error('…')` that stood at 21 read
 * sites, twenty of which discarded the ProblemDetails body and substituted a literal — so a failed
 * read could never render the support reference `ApiErrorNotice` was built to show. The literal
 * survives as `fallbackMessage`: it is better copy than `Request failed (500).` when the server
 * sends an empty body, and worse than the server's own `detail` when it sends one.
 *
 * Three modules had already converged on this signature independently (banking, onboarding and
 * operations each held a byte-identical private `unwrap`); those three are now this one.
 */
export async function unwrap<T>(call: Promise<ApiResult<T>>, fallbackMessage: string): Promise<T> {
  const { data, error, response } = await call;
  if (data !== undefined && data !== null) return data;
  throw toApiError(error, response?.status ?? 0, fallbackMessage);
}

/** Blob → object URL → anchor click → revoke. Deliberately not exported; see {@link download}. */
function saveBlob(blob: Blob, filename: string): void {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = filename;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}

/**
 * Fetches a file through the authenticated client and hands it to the browser as a download.
 *
 * The thunk absorbs the variance across call sites — which generated function, which path/query,
 * whether the body arrives as a blob or has to be built from JSON — while this helper keeps the one
 * part that was copied verbatim five times: the success check, the `instanceof Blob` guard, and the
 * anchor dance.
 *
 * There is deliberately no lower-level `download(blob, filename)` overload. Exporting the anchor
 * dance on its own is what let five sites grow four different error contracts for one operation
 * shape, and no caller needs it.
 */
export async function download(
  // `unknown`, not `Blob`: the generated functions declare their body from the OpenAPI response
  // type, and `parseAs: 'blob'` is a runtime option that does not narrow it. The `instanceof` guard
  // below is what establishes the type, exactly as the five hand-written copies did.
  call: () => Promise<ApiResult<unknown>>,
  filename: string,
  fallbackMessage = 'Download failed.',
): Promise<void> {
  const { data, error, response } = await call();
  if (error || !(data instanceof Blob)) {
    throw toApiError(error, response?.status ?? 0, fallbackMessage);
  }
  saveBlob(data, filename);
}
