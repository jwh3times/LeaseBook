/**
 * TanStack Query hooks for the M6 operations run engine (§M6 / ADR-019).
 * Mirrors the pattern in web/src/features/banking/banking.ts.
 */
import { useMutation, useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import {
  getApiOperationsRuns,
  getApiOperationsRunsByTypePreview,
  postApiOperationsRunsByTypeConfirm,
  primeCsrf,
  unwrap,
  type ApiError,
  type BulkRunDetailResponse,
  type BulkRunSpa,
  type ConfirmRunRequest,
  type PreviewRowSpa,
  type RunHistoryResponse,
  type RunPreviewSpaResponse,
  type RunResultSpaResponse,
} from '@/api';

// ─── Types mirroring the SPA-response records ─────────────────────────────────

export type {
  BulkRunDetailResponse,
  BulkRunSpa,
  ConfirmRunRequest,
  PreviewRowSpa,
  RunHistoryResponse,
  RunPreviewSpaResponse,
  RunResultSpaResponse,
};

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
    queryFn: () =>
      unwrap(
        getApiOperationsRunsByTypePreview({ path: { type }, query: { year, month } }),
        `Failed to load ${type} preview`,
      ),
  });
}

/** All past runs for this org, newest first. */
export function useRunHistory(): UseQueryResult<RunHistoryResponse> {
  return useQuery({
    queryKey: runHistoryKey(),
    queryFn: () => unwrap(getApiOperationsRuns(), 'Failed to load run history'),
  });
}

// ─── Mutations ────────────────────────────────────────────────────────────────

/** Error shape from the operations API (400 / 409 / 500 ProblemDetails). */
export type RunError = ApiError;

/** The 409 the server raises when the capability set moved between preview and confirm. */
export const CAPABILITIES_CHANGED = 'capabilities_changed';

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
        postApiOperationsRunsByTypeConfirm({
          path: { type },
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
        `Failed to confirm the ${type} run`,
      );
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: runHistoryKey() });
    },
    onError: (error, { year, month }) => {
      // A stale token means the amounts on screen are no longer what would post, so the preview
      // itself is the thing that is wrong — refetch it here rather than in each screen, or the
      // operator's only recourse would be to re-click Confirm with the same stale token forever.
      // Exact match, never a prefix. The server's other capability conflict,
      // `capabilities_changed_since_prior_run`, starts with this same string and must NOT refetch:
      // no fresh preview can change what an already committed run recorded, so a prefix match would
      // spin the preview forever against a conflict only a human decision clears. It is deliberately
      // not named as a constant here — there is nothing for this hook to do with it, and an exported
      // constant with no consumer reads as a handler that does not exist.
      if (error.code === CAPABILITIES_CHANGED) {
        void queryClient.invalidateQueries({ queryKey: runPreviewKey(type, year, month) });
      }
    },
  });
}
