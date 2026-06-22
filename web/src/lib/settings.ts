import { useMutation, useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import { api, primeCsrf, type components } from '@/api';

export type OrgSettings = components['schemas']['OrgSettingsResponse'];
export type BankAccount = components['schemas']['BankAccountResponse'];
type UpdateOrgBody = components['schemas']['UpdateOrgSettings'];
type CreateBankBody = components['schemas']['CreateBankAccount'];

export const orgSettingsKey = ['org-settings'] as const;

export function useOrgSettings(): UseQueryResult<OrgSettings> {
  return useQuery({
    queryKey: orgSettingsKey,
    queryFn: async () => {
      const { data, error } = await api.GET('/api/settings/org');
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
      const { data, error } = await api.PUT('/api/settings/org', { body });
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
      const { data, error } = await api.GET('/api/settings/banks', {
        params: { query: activeOnly ? { activeOnly: true } : {} },
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
      const { data, error } = await api.PUT('/api/settings/banks/{id}/active', {
        params: { path: { id } },
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
      const { data, error } = await api.POST('/api/settings/banks', { body });
      if (error || !data) throw new Error('Failed to create the bank account');
      return data;
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ['bank-accounts'] }),
  });
}
