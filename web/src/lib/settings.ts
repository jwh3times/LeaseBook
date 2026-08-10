import { useMutation, useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import {
  getApiSettingsBanks,
  getApiSettingsOrg,
  postApiSettingsBanks,
  primeCsrf,
  putApiSettingsBanksByIdActive,
  putApiSettingsOrg,
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
    queryFn: async () => {
      const { data, error } = await getApiSettingsOrg();
      if (error || !data) throw new Error('Failed to load settings');
      return data;
    },
    staleTime: 60_000,
  });
}

export function useUpdateOrgSettings() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (body: UpdateOrgBody) => {
      await primeCsrf();
      const { data, error } = await putApiSettingsOrg({ body });
      if (error || !data) throw new Error('Failed to save settings');
      return data;
    },
    onSuccess: (data) => qc.setQueryData(orgSettingsKey, data),
  });
}

export function useBankAccounts(activeOnly = false): UseQueryResult<BankAccount[]> {
  return useQuery({
    queryKey: ['bank-accounts', { activeOnly }],
    queryFn: async () => {
      const { data, error } = await getApiSettingsBanks({
        query: activeOnly ? { activeOnly: true } : {},
      });
      if (error || !data) throw new Error('Failed to load bank accounts');
      return data;
    },
  });
}

export function useSetBankAccountActive() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async ({ id, isActive }: { id: string; isActive: boolean }) => {
      await primeCsrf();
      const { data, error } = await putApiSettingsBanksByIdActive({
        path: { id },
        body: { isActive },
      });
      if (error || !data) throw error ?? new Error('Failed to update the bank account');
      return data;
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ['bank-accounts'] }),
  });
}

export function useCreateBankAccount() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (body: CreateBankBody) => {
      await primeCsrf();
      const { data, error } = await postApiSettingsBanks({ body });
      if (error || !data) throw new Error('Failed to create the bank account');
      return data;
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ['bank-accounts'] }),
  });
}
