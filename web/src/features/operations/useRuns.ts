/**
 * TanStack Query hooks for the M6 operations run engine (§M6 / ADR-019).
 * Mirrors the pattern in web/src/features/banking/banking.ts.
 */
import { useMutation, useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import { api, primeCsrf, type components } from '@/api';
import { toApiError, type ApiError } from '@/lib/apiError';

// ─── Types mirroring the SPA-response records ─────────────────────────────────

export type RunPreviewSpaResponse = components['schemas']['RunPreviewSpaResponse'];
export type PreviewRowSpa = components['schemas']['PreviewRowSpa'];
export type RunResultSpaResponse = components['schemas']['RunResultSpaResponse'];
export type RunHistoryResponse = components['schemas']['RunHistoryResponse'];
export type BulkRunSpa = components['schemas']['BulkRunSpa'];
export type BulkRunDetailResponse = components['schemas']['BulkRunDetailResponse'];
export type ConfirmRunRequest = components['schemas']['ConfirmRunRequest'];

export type RunType = 'rent' | 'latefee' | 'disbursement';

// ─── Query keys ───────────────────────────────────────────────────────────────

export const runPreviewKey = (type: RunType, year: number, month: number) =>
  ['operations', 'preview', type, year, month] as const;

export const runHistoryKey = () => ['operations', 'history'] as const;

// ─── Queries ──────────────────────────────────────────────────────────────────

/** Preview what a run of the given type would post for the given period. */
export function useRunPreview(
  type: RunType,
  year: number,
  month: number,
): UseQueryResult<RunPreviewSpaResponse> {
  return useQuery({
    queryKey: runPreviewKey(type, year, month),
    queryFn: async () => {
      const { data, error } = await api.GET('/api/operations/runs/{type}/preview', {
        params: { path: { type }, query: { year, month } },
      });
      if (error || !data) throw new Error(`Failed to load ${type} preview`);
      return data;
    },
  });
}

/** All past runs for this org, newest first. */
export function useRunHistory(): UseQueryResult<RunHistoryResponse> {
  return useQuery({
    queryKey: runHistoryKey(),
    queryFn: async () => {
      const { data, error } = await api.GET('/api/operations/runs');
      if (error || !data) throw new Error('Failed to load run history');
      return data;
    },
  });
}

// ─── Mutations ────────────────────────────────────────────────────────────────

/** Error shape from the operations API (400 / 409 / 500 ProblemDetails). */
export type RunError = ApiError;
const toRunError = toApiError;

async function unwrap<T>(
  call: Promise<{ data?: T; error?: unknown; response: Response }>,
): Promise<T> {
  const { data, error, response } = await call;
  if (data !== undefined && data !== null) return data;
  throw toRunError(error, response.status);
}

/** The 409 the server raises when the capability set moved between preview and confirm. */
export const CAPABILITIES_CHANGED = 'capabilities_changed';

/**
 * The 409 the server raises when an EARLIER committed run for this period ran under a different
 * money-path capability state (ADR-028). Deliberately not handled like the one above: refetching
 * the preview cannot change what an already committed run recorded, so an auto-refetch would loop
 * and tell the operator nothing. It surfaces as a message, and clearing it is a decision someone
 * has to make - acknowledge the change deliberately, or restore the earlier feature state.
 */
export const CAPABILITIES_CHANGED_SINCE_PRIOR_RUN = 'capabilities_changed_since_prior_run';

/**
 * Confirm a run for the given type, period, and selected target ids.
 * Invalidates run history on success.
 *
 * `capabilitiesVersion` is the opaque token the preview handed back, echoed verbatim. The server
 * compares it against what it resolves itself and rejects a mismatch with 409 — the caller only
 * carries the value, it never interprets it.
 */
export function useConfirmRun(type: RunType) {
  const queryClient = useQueryClient();
  return useMutation<
    RunResultSpaResponse,
    RunError,
    { year: number; month: number; selectedTargetIds: string[]; capabilitiesVersion: string }
  >({
    mutationFn: async ({ year, month, selectedTargetIds, capabilitiesVersion }) => {
      await primeCsrf();
      return unwrap(
        api.POST('/api/operations/runs/{type}/confirm', {
          params: { path: { type } },
          body: {
            year,
            month,
            selectedTargetIds,
            capabilitiesVersion,
            // Stated, not omitted. Overriding the cross-run period guard is a money decision and
            // this screen offers no affordance for it, so the client says so explicitly rather
            // than leaving the answer to a server-side default.
            acknowledgeCapabilityChange: false,
          },
        }),
      );
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: runHistoryKey() });
    },
    onError: (error, { year, month }) => {
      // A stale token means the amounts on screen are no longer what would post, so the preview
      // itself is the thing that is wrong — refetch it here rather than in each screen, or the
      // operator's only recourse would be to re-click Confirm with the same stale token forever.
      // Exact match, never a prefix: capabilities_changed_since_prior_run starts with this code
      // and must NOT refetch, because no fresh preview can change what a committed run recorded.
      if (error.code === CAPABILITIES_CHANGED) {
        void queryClient.invalidateQueries({ queryKey: runPreviewKey(type, year, month) });
      }
    },
  });
}
