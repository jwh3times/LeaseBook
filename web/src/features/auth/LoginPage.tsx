import { useEffect, useState, type FormEvent } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router';
import { postApiAuthLogin, postApiAuthMfa, postApiAuthMfaRecovery, primeCsrf, unwrap } from '@/api';
import { Button, Card, Input } from '@/design';
import { sessionQueryKey } from './useSession';

export function LoginPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [mfaToken, setMfaToken] = useState<string | null>(null);
  const [useRecoveryCode, setUseRecoveryCode] = useState(false);
  const [code, setCode] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Auth-state priming: the antiforgery token is user-bound, so each side of the sign-in
  // transition needs its own. Mount refreshes the anonymous token (a signed-out cookie may still
  // hold the previous user's); sign-in success refreshes it again for the new user so the first
  // mutation does not pay a rejection replay. Both are latency hiding, not correctness — the api
  // client primes on demand and replays once on a stale token.
  useEffect(() => {
    void primeCsrf();
  }, []);

  async function finishSignIn() {
    void primeCsrf();
    await queryClient.invalidateQueries({ queryKey: sessionQueryKey });
    void navigate('/dashboard', { replace: true });
  }

  async function submitPassword(event: FormEvent) {
    event.preventDefault();
    setError(null);
    setBusy(true);
    try {
      const data = await unwrap(
        postApiAuthLogin({ body: { email, password } }),
        'Invalid email or password.',
      );
      setPassword('');
      if (data.status === 'mfa-required') {
        setMfaToken(data.mfaToken);
        return;
      }
      await finishSignIn();
    } catch {
      setError('Invalid email or password.');
    } finally {
      setBusy(false);
    }
  }

  async function submitMfa(event: FormEvent) {
    event.preventDefault();
    if (!mfaToken) return;
    setError(null);
    setBusy(true);
    try {
      await unwrap(
        (useRecoveryCode ? postApiAuthMfaRecovery : postApiAuthMfa)({ body: { mfaToken, code } }),
        'Invalid authentication code.',
      );
      setCode('');
      await finishSignIn();
    } catch {
      setError(useRecoveryCode ? 'Invalid recovery code.' : 'Invalid authentication code.');
    } finally {
      setBusy(false);
    }
  }

  const onMfaStep = mfaToken !== null;

  return (
    <div
      className="row"
      style={{ minHeight: '100vh', justifyContent: 'center', background: 'var(--bg)' }}
    >
      <Card pad className="pf-fade">
        <div style={{ width: 320 }} className="col gap16">
          <div className="row gap10">
            <div className="pf-logo">
              <span />
            </div>
            <div className="col">
              <b style={{ fontSize: 16 }}>LeaseBook</b>
              <span className="t3 fs12">{onMfaStep ? 'Two-factor verification' : 'Sign in'}</span>
            </div>
          </div>

          {onMfaStep ? (
            <form className="col gap12" onSubmit={submitMfa}>
              <label className="col gap6 fs13 t2">
                {useRecoveryCode ? 'Recovery code' : 'Authentication code'}
                <Input
                  inputMode={useRecoveryCode ? 'text' : 'numeric'}
                  autoComplete="one-time-code"
                  value={code}
                  onChange={(e) => setCode(e.target.value)}
                  placeholder={useRecoveryCode ? 'Recovery code' : '123456'}
                  autoFocus
                />
              </label>
              {error && (
                <span role="alert" className="fs13" style={{ color: 'var(--neg)' }}>
                  {error}
                </span>
              )}
              <Button type="submit" variant="primary" disabled={busy}>
                {busy ? 'Verifying…' : 'Verify'}
              </Button>
              <Button
                type="button"
                disabled={busy}
                onClick={() => {
                  setUseRecoveryCode(!useRecoveryCode);
                  setCode('');
                  setError(null);
                }}
              >
                {useRecoveryCode ? 'Use authenticator code' : 'Use a recovery code'}
              </Button>
            </form>
          ) : (
            <form className="col gap12" onSubmit={submitPassword}>
              <label className="col gap6 fs13 t2">
                Email
                <Input
                  type="email"
                  autoComplete="username"
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                  placeholder="you@example.com"
                  autoFocus
                />
              </label>
              <label className="col gap6 fs13 t2">
                Password
                <Input
                  type="password"
                  autoComplete="current-password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  placeholder="••••••••••••"
                />
              </label>
              {error && (
                <span role="alert" className="fs13" style={{ color: 'var(--neg)' }}>
                  {error}
                </span>
              )}
              <Button type="submit" variant="primary" disabled={busy}>
                {busy ? 'Signing in…' : 'Sign in'}
              </Button>
            </form>
          )}
        </div>
      </Card>
    </div>
  );
}
