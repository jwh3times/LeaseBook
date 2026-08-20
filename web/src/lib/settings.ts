import { useMutation, useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import {
  getApiSettingsBanks,
  getApiSettingsOrg,
  postApiSettingsBanks,
  primeCsrf,
  putApiSettingsBanksByIdActive,
  putApiSettingsOrg,
  unwrap,
  type BankAccountResponse,
  type CreateBankAccount,
  type OrgSettingsResponse,
  type UpdateOrgSettings,
} from '@/api';

export type OrgSettings = OrgSettingsResponse;
export type BankAccount = BankAccountResponse;
type UpdateOrgBody = UpdateOrgSettings;
type CreateBankBody = CreateBankAccount;

export const orgSettingsKey = ['org-settings'] as const;

export function useOrgSettings(): UseQueryResult<OrgSettings> {
  return useQuery({
    queryKey: orgSettingsKey,
    queryFn: () => unwrap(getApiSettingsOrg(), 'Failed to load settings'),
    staleTime: 60_000,
  });
}

export function useUpdateOrgSettings() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (body: UpdateOrgBody) => {
      await primeCsrf();
      return unwrap(putApiSettingsOrg({ body }), 'Failed to save settings');
    },
    onSuccess: (data) => qc.setQueryData(orgSettingsKey, data),
  });
}

export function useBankAccounts(activeOnly = false): UseQueryResult<BankAccount[]> {
  return useQuery({
    queryKey: ['bank-accounts', { activeOnly }],
    queryFn: () =>
      unwrap(
        getApiSettingsBanks({ query: activeOnly ? { activeOnly: true } : {} }),
        'Failed to load bank accounts',
      ),
  });
}

export function useSetBankAccountActive() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async ({ id, isActive }: { id: string; isActive: boolean }) => {
      await primeCsrf();
      return unwrap(
        putApiSettingsBanksByIdActive({ path: { id }, body: { isActive } }),
        'Failed to update the bank account',
      );
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ['bank-accounts'] }),
  });
}

export function useCreateBankAccount() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (body: CreateBankBody) => {
      await primeCsrf();
      return unwrap(postApiSettingsBanks({ body }), 'Failed to create the bank account');
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ['bank-accounts'] }),
  });
}
