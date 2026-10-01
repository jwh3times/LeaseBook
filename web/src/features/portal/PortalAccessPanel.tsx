import { useQuery } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { Link } from 'react-router';
import {
  asApiError,
  getApiPortalAccessByPersonaByTargetId,
  postApiPortalAccessByPersonaByTargetId,
  postApiPortalAccessByPersonaByTargetIdUsersByUserIdRevoke,
  unwrap,
  type ApiError,
  type PortalAccessUser,
} from '@/api';
import {
  postApiPortalAccessInvitationsByIdCancel,
  postApiPortalAccessInvitationsByIdReplace,
  postApiPortalAccessInvitationsByIdRetry,
} from '@/api';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { Modal } from '@/components/Modal';
import { QueryErrorState } from '@/components/QueryErrorState';
import { Badge, Button, Card, Input } from '@/design';

function deliveryLabel(status: string) {
  return (
    (
      {
        pending: 'Queued for test inbox',
        delivered: 'Available in test inbox',
        failed: 'Test delivery failed',
      } as Record<string, string>
    )[status] ?? 'Test delivery pending'
  );
}

export function PortalAccessPanel({
  persona,
  targetId,
}: {
  persona: 'tenant' | 'owner';
  targetId: string;
}) {
  const [email, setEmail] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [revoking, setRevoking] = useState<PortalAccessUser | null>(null);
  const query = useQuery({
    queryKey: ['portal-access', persona, targetId],
    queryFn: () =>
      unwrap(
        getApiPortalAccessByPersonaByTargetId({ path: { persona, targetId } }),
        'Could not load portal access.',
      ),
    refetchInterval: (current) =>
      current.state.status !== 'error' &&
      current.state.data?.invitations.some(
        (invitation) => invitation.status === 'pending' && invitation.deliveryStatus === 'pending',
      )
        ? 2000
        : false,
  });
  async function invite(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await unwrap(
        postApiPortalAccessByPersonaByTargetId({ path: { persona, targetId }, body: { email } }),
        'Could not create the invitation.',
      );
      setEmail('');
      await query.refetch();
    } catch (e) {
      setError(asApiError(e));
    } finally {
      setBusy(false);
    }
  }
  async function revoke() {
    if (!revoking) return;
    setBusy(true);
    setError(null);
    try {
      await unwrap(
        postApiPortalAccessByPersonaByTargetIdUsersByUserIdRevoke({
          path: { persona, targetId, userId: revoking.userId },
        }),
        'Could not revoke portal access.',
      );
      setRevoking(null);
      await query.refetch();
    } catch (e) {
      setError(asApiError(e));
    } finally {
      setBusy(false);
    }
  }
  async function changeInvitation(id: string, action: 'replace' | 'cancel' | 'retry') {
    setBusy(true);
    setError(null);
    try {
      if (action === 'replace')
        await unwrap(
          postApiPortalAccessInvitationsByIdReplace({ path: { id } }),
          'Could not replace the invitation.',
        );
      else if (action === 'cancel')
        await unwrap(
          postApiPortalAccessInvitationsByIdCancel({ path: { id } }),
          'Could not cancel the invitation.',
        );
      else
        await unwrap(
          postApiPortalAccessInvitationsByIdRetry({ path: { id } }),
          'Could not retry test delivery.',
        );
      await query.refetch();
    } catch (e) {
      setError(asApiError(e));
    } finally {
      setBusy(false);
    }
  }
  return (
    <Card pad>
      <h3>Portal access</h3>
      {query.isError ? (
        <QueryErrorState
          query={query}
          title="Couldn’t load portal access"
          fallback="Could not load portal access."
        />
      ) : query.isPending ? (
        <p role="status">Loading portal access…</p>
      ) : (
        <div className="col gap12">
          <p className="t3 fs13">
            Invite only a person authorized to see this {persona}’s financial records.
          </p>
          {import.meta.env.DEV && query.data.canManage && (
            <Link to="/portal/test-inbox" style={{ color: 'var(--text)' }}>
              Open test inbox
            </Link>
          )}
          {query.data.canManage ? (
            <form className="row gap8" onSubmit={invite}>
              <label className="col gap6">
                Invitation email
                <Input
                  type="email"
                  required
                  maxLength={256}
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                />
              </label>
              <Button variant="primary" type="submit" disabled={busy}>
                Send invitation
              </Button>
            </form>
          ) : (
            <p>Portal access management is unavailable for this account.</p>
          )}
          {error && <ApiErrorNotice error={error} />}
          <h4>Active users</h4>
          {query.data.users.length === 0 ? (
            <p>No active portal users.</p>
          ) : (
            query.data.users.map((user) => (
              <div className="row gap8" key={user.userId}>
                <span>
                  {user.displayName} · {user.email}
                </span>
                {query.data.canManage && (
                  <Button
                    disabled={busy}
                    aria-label={`Revoke access for ${user.email}`}
                    onClick={() => {
                      setError(null);
                      setRevoking(user);
                    }}
                  >
                    Revoke access
                  </Button>
                )}
              </div>
            ))
          )}
          <h4>Invitations</h4>
          {query.data.invitations.length === 0 ? (
            <p>No invitations yet.</p>
          ) : (
            query.data.invitations.map((invitation) => (
              <div key={invitation.id} className="col gap6">
                <strong>{invitation.email}</strong>
                <span>
                  <Badge dot>{invitation.status}</Badge> ·{' '}
                  <span>{deliveryLabel(invitation.deliveryStatus)}</span>
                </span>
                <span className="t3 fs13">
                  Expires {new Date(invitation.expiresAt).toLocaleString()}
                </span>
                {query.data.canManage &&
                  (invitation.status === 'pending' || invitation.status === 'expired') && (
                    <div className="row gap8">
                      <Button
                        disabled={busy}
                        onClick={() => void changeInvitation(invitation.id, 'replace')}
                      >
                        Replace invitation
                      </Button>
                      <Button
                        disabled={busy}
                        onClick={() => void changeInvitation(invitation.id, 'cancel')}
                      >
                        Cancel invitation
                      </Button>
                      {invitation.status === 'pending' &&
                        invitation.deliveryStatus === 'failed' && (
                          <Button
                            disabled={busy}
                            onClick={() => void changeInvitation(invitation.id, 'retry')}
                          >
                            Retry test delivery
                          </Button>
                        )}
                    </div>
                  )}
              </div>
            ))
          )}
        </div>
      )}
      {revoking && (
        <Modal
          title="Revoke portal access"
          onClose={() => {
            if (!busy) setRevoking(null);
          }}
          footer={
            <>
              <Button disabled={busy} onClick={() => setRevoking(null)}>
                Keep access
              </Button>
              <Button disabled={busy} variant="primary" onClick={() => void revoke()}>
                Revoke access
              </Button>
            </>
          }
        >
          <p className="pf-pad">
            {revoking.email} will lose access to this portal. Pending invitations for this account
            will be cancelled.
          </p>
          {error && <ApiErrorNotice error={error} />}
        </Modal>
      )}
    </Card>
  );
}
