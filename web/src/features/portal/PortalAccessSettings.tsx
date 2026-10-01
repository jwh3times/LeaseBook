import { useEffect, useState, type FormEvent } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { asApiError, putApiSettingsPortalAccess, unwrap, type ApiError } from '@/api';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { QueryErrorState } from '@/components/QueryErrorState';
import { Button, Card } from '@/design';
import { useSession } from '@/features/auth/useSession';
import { orgSettingsKey, type OrgSettings } from '@/lib/settings';

export function PortalAccessSettings({ initial }: { initial: OrgSettings }) {
  const session = useSession();
  const queries = useQueryClient();
  const update = useMutation({
    mutationFn: (staffCanManagePortalAccess: boolean) =>
      unwrap(
        putApiSettingsPortalAccess({ body: { staffCanManagePortalAccess } }),
        'Failed to save portal permissions',
      ),
    onSuccess: (data) => queries.setQueryData(orgSettingsKey, data),
  });
  const [allowed, setAllowed] = useState(initial.staffCanManagePortalAccess ?? true);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  useEffect(
    () => setAllowed(initial.staffCanManagePortalAccess ?? true),
    [initial.staffCanManagePortalAccess],
  );
  const admin = session.data?.role === 'PMAdmin';
  async function save(event: FormEvent) {
    event.preventDefault();
    setSaved(false);
    setError(null);
    try {
      await update.mutateAsync(allowed);
      await queries.invalidateQueries({ queryKey: ['portal-access'] });
      setSaved(true);
    } catch (e) {
      setError(asApiError(e));
    }
  }
  return (
    <Card pad>
      <h3>Portal permissions</h3>
      {session.isError ? (
        <QueryErrorState
          query={session}
          title="Couldn’t check your permissions"
          fallback="Could not check your permissions."
        />
      ) : session.isPending ? (
        <p role="status">Checking your permissions…</p>
      ) : (
        <form className="col gap12" onSubmit={save}>
          <label className="row gap8">
            <input
              type="checkbox"
              checked={allowed}
              disabled={!admin || update.isPending}
              onChange={(e) => {
                setAllowed(e.target.checked);
                setSaved(false);
              }}
            />
            Allow staff to manage portal invitations and access
          </label>
          <p className="t3 fs13">
            Administrators always retain access. Only an administrator can change this setting.
          </p>
          {error && <ApiErrorNotice error={error} />}
          {admin && (
            <Button type="submit" disabled={update.isPending}>
              Save portal permissions
            </Button>
          )}
          {saved && <p role="status">Portal permissions saved.</p>}
        </form>
      )}
    </Card>
  );
}
