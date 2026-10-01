import { useEffect, useState, type FormEvent } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Link, useLocation, useNavigate } from 'react-router';
import {
  asApiError,
  postApiPortalEnrollmentAccept,
  postApiPortalEnrollmentInspect,
  unwrap,
  type ApiError,
  type PortalEnrollmentInspection,
} from '@/api';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { ErrorAction } from '@/components/ErrorAction';
import { Button, Card, Input } from '@/design';
import { LoginPage } from '@/features/auth/LoginPage';
import { signOutCurrentSession } from '@/features/auth/signOut';

/** Proof and credentials live only in this component, never in query or mutation caches. */
export function PortalEnrollmentPage() {
  const location = useLocation();
  const navigate = useNavigate();
  const queries = useQueryClient();
  const [token, setToken] = useState(
    () => new URLSearchParams(location.hash.slice(1)).get('token') ?? '',
  );
  const [inspection, setInspection] = useState<PortalEnrollmentInspection | null>(null);
  const [name, setName] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<ApiError | null>(null);
  const [busy, setBusy] = useState(false);
  const [accepted, setAccepted] = useState(false);
  const [signingIn, setSigningIn] = useState(false);
  const [revision, setRevision] = useState(0);

  useEffect(() => {
    if (location.hash) {
      setToken(new URLSearchParams(location.hash.slice(1)).get('token') ?? '');
      setInspection(null);
      setPassword('');
      setName('');
      setError(null);
      setAccepted(false);
      setSigningIn(false);
      void navigate('/portal/enroll', { replace: true });
    }
  }, [location.hash, navigate]);

  useEffect(() => {
    if (!token) return;
    let active = true;
    void unwrap(
      postApiPortalEnrollmentInspect({ body: { token } }),
      'Could not check the invitation.',
    )
      .then((data) => {
        if (active) {
          setInspection(data);
          setName(data.displayName);
        }
      })
      .catch((e) => {
        if (active) setError(asApiError(e));
      });
    return () => {
      active = false;
    };
  }, [token, revision]);

  async function accept(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await unwrap(
        postApiPortalEnrollmentAccept({
          body: inspection?.requiresPassword ? { token, displayName: name, password } : { token },
        }),
        'Could not accept the invitation.',
      );
      setPassword('');
      setToken('');
      setInspection(null);
      setAccepted(true);
    } catch (e) {
      setError(asApiError(e));
    } finally {
      setBusy(false);
    }
  }

  function recheck() {
    setInspection(null);
    setError(null);
    setPassword('');
    setRevision((value) => value + 1);
  }

  async function signOutAndRecheck() {
    setBusy(true);
    try {
      await signOutCurrentSession();
      queries.clear();
      recheck();
    } catch (e) {
      setError(asApiError(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="col gap16 pf-pad" style={{ maxWidth: 560, margin: '0 auto' }}>
      <h1>Join your LeaseBook portal</h1>
      <Card pad>
        {accepted ? (
          <div className="col gap12">
            <p role="status">Your portal access is ready.</p>
            <Link to="/login" style={{ color: 'var(--text)' }}>
              Sign in to your portal
            </Link>
          </div>
        ) : !token ? (
          <p>
            Open the invitation link provided by your property manager. If you refreshed this page,
            reopen that link.
          </p>
        ) : (
          <div className="col gap12">
            {error && <ApiErrorNotice error={error} kind={inspection ? 'write' : 'read'} />}
            {!inspection && !error && <p role="status">Checking invitation…</p>}
            {inspection?.requiresSignIn ? (
              <div className="col gap12">
                <p>
                  Sign in as <strong>{inspection.email}</strong> to accept this invitation. Your
                  existing password and account security stay in place.
                </p>
                {signingIn ? (
                  <LoginPage
                    onSignedIn={() => {
                      setSigningIn(false);
                      setInspection(null);
                      setError(null);
                      setRevision((value) => value + 1);
                    }}
                  />
                ) : (
                  <Button onClick={() => setSigningIn(true)}>Sign in to accept</Button>
                )}
              </div>
            ) : (
              inspection && (
                <form className="col gap12" onSubmit={accept}>
                  <p>
                    You are accepting {inspection.persona} portal access for{' '}
                    <strong>{inspection.email}</strong>.
                  </p>
                  {inspection.requiresPassword && (
                    <>
                      <label className="col gap6">
                        Your name
                        <Input
                          required
                          maxLength={120}
                          autoComplete="name"
                          value={name}
                          onChange={(e) => setName(e.target.value)}
                        />
                      </label>
                      <label className="col gap6">
                        New password
                        <Input
                          type="password"
                          required
                          minLength={12}
                          maxLength={256}
                          autoComplete="new-password"
                          value={password}
                          onChange={(e) => setPassword(e.target.value)}
                        />
                      </label>
                      <p className="t3 fs13">
                        Use at least 12 characters, including uppercase and lowercase letters, a
                        number and a symbol.
                      </p>
                    </>
                  )}
                  <Button variant="primary" type="submit" disabled={busy}>
                    {busy ? 'Accepting…' : 'Accept invitation'}
                  </Button>
                </form>
              )
            )}
            {error?.code === 'incompatible_session' ? (
              <Button disabled={busy} onClick={() => void signOutAndRecheck()}>
                Sign out everywhere and continue
              </Button>
            ) : !inspection && error && error.code !== 'invalid_invitation' ? (
              <ErrorAction error={error} onRetry={recheck} retrying={busy} />
            ) : (
              error?.code === 'invalid_invitation' && (
                <p>
                  Ask your property manager for a new invitation if this link has expired or was
                  cancelled.
                </p>
              )
            )}
          </div>
        )}
      </Card>
    </main>
  );
}
