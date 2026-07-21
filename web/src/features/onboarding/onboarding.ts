/**
 * TanStack Query hooks for M7 import-first onboarding wizard (WP-5).
 * Mirrors the pattern in web/src/features/operations/useRuns.ts.
 */
import { useMutation, useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import { api, primeCsrf, type components } from '@/api';
import { toApiError, type ApiError } from '@/lib/apiError';

// ─── Types ────────────────────────────────────────────────────────────────────

export type OnboardingStatusResponse = components['schemas']['OnboardingStatusResponse'];
export type ImportBatchResult = components['schemas']['ImportBatchResult'];
export type ImportBatchError = components['schemas']['ImportBatchError'];
export type EntityImportRequest = components['schemas']['EntityImportRequest'];
export type BalanceImportRequest = components['schemas']['BalanceImportRequest'];
export type VerificationRequestDto = components['schemas']['VerificationRequestDto'];
export type VerificationReport = components['schemas']['VerificationReport'];
export type VarianceLine = components['schemas']['VarianceLine'];
export type BankBalanceDto = components['schemas']['BankBalanceDto'];

export type EntityKind = 'owners' | 'properties' | 'units' | 'tenants_leases';
export type BalanceKind =
  | 'owner_balances'
  | 'deposit_liabilities'
  | 'bank_balances'
  | 'tenant_receivables'
  | 'held_pm_fees';

// ─── Query keys ───────────────────────────────────────────────────────────────

export const onboardingStatusKey = () => ['onboarding', 'status'] as const;

// ─── Error types ──────────────────────────────────────────────────────────────

export type OnboardingError = ApiError;
const toOnboardingError = toApiError;

async function unwrap<T>(
  call: Promise<{ data?: T; error?: unknown; response: Response }>,
): Promise<T> {
  const { data, error, response } = await call;
  if (data !== undefined && data !== null) return data;
  throw toOnboardingError(error, response.status);
}

// ─── Queries ──────────────────────────────────────────────────────────────────

/** Current wizard step state — derived from server-side flags. */
export function useOnboardingStatus(): UseQueryResult<OnboardingStatusResponse> {
  return useQuery({
    queryKey: onboardingStatusKey(),
    queryFn: async () => {
      const { data, error } = await api.GET('/api/onboarding/status');
      if (error || !data) throw new Error('Failed to load onboarding status');
      return data;
    },
  });
}

// ─── Mutations ────────────────────────────────────────────────────────────────

/** Import entity CSV (owners | properties | units | tenants_leases). Invalidates status. */
export function useImportEntities(kind: EntityKind) {
  const queryClient = useQueryClient();
  return useMutation<ImportBatchResult, OnboardingError, EntityImportRequest>({
    mutationFn: async (body) => {
      await primeCsrf();
      return unwrap(
        api.POST('/api/onboarding/import/{kind}', {
          params: { path: { kind } },
          body,
        }),
      );
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: onboardingStatusKey() });
    },
  });
}

/** Import balance CSV (owner_balances | deposit_liabilities | bank_balances | tenant_receivables). Invalidates status. */
export function useImportBalances(kind: BalanceKind) {
  const queryClient = useQueryClient();
  return useMutation<ImportBatchResult, OnboardingError, BalanceImportRequest>({
    mutationFn: async (body) => {
      await primeCsrf();
      return unwrap(
        api.POST('/api/onboarding/import-balances/{kind}', {
          params: { path: { kind } },
          body,
        }),
      );
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: onboardingStatusKey() });
    },
  });
}

export type ImportOutcomeCounts = components['schemas']['ImportOutcomeCounts'];

/** Corrected re-import (supersede) for an already-imported balance kind. Invalidates status. */
export function useSupersedeBalances(kind: BalanceKind) {
  const queryClient = useQueryClient();
  return useMutation<ImportBatchResult, OnboardingError, BalanceImportRequest>({
    mutationFn: async (body) => {
      await primeCsrf();
      return unwrap(
        api.POST('/api/onboarding/import-balances/{kind}/supersede', {
          params: { path: { kind } },
          body,
        }),
      );
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: onboardingStatusKey() });
    },
  });
}

/** Run verification — operator supplies AppFolio closing figures. Invalidates status. */
export function useVerify() {
  const queryClient = useQueryClient();
  return useMutation<VerificationReport, OnboardingError, VerificationRequestDto>({
    mutationFn: async (body) => {
      await primeCsrf();
      return unwrap(api.POST('/api/onboarding/verification', { body }));
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: onboardingStatusKey() });
    },
  });
}

/** Sign off on a tied verification report. Surfaces 409 not_tied as an OnboardingError. Invalidates status. */
export function useSignoff() {
  const queryClient = useQueryClient();
  return useMutation<void, OnboardingError, { id: string }>({
    mutationFn: async ({ id }) => {
      await primeCsrf();
      const { error, response } = await api.POST('/api/onboarding/verification/{id}/signoff', {
        params: { path: { id } },
      });
      if (response.ok) return;
      throw toOnboardingError(error, response.status);
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: onboardingStatusKey() });
    },
  });
}
