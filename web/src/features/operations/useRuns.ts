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

/**
 * Confirm a run for the given type, period, and selected target ids.
 * Invalidates run history on success.
 */
export function useConfirmRun(type: RunType) {
  const queryClient = useQueryClient();
  return useMutation<
    RunResultSpaResponse,
    RunError,
    { year: number; month: number; selectedTargetIds: string[] }
  >({
    mutationFn: async ({ year, month, selectedTargetIds }) => {
      await primeCsrf();
      return unwrap(
        api.POST('/api/operations/runs/{type}/confirm', {
          params: { path: { type } },
          body: { year, month, selectedTargetIds },
        }),
      );
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: runHistoryKey() });
    },
  });
}
