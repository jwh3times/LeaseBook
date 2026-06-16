import { useEffect, useState, type FormEvent } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { api, primeCsrf } from '@/api';
import { Button, Card, Input } from '@/design';
import { sessionQueryKey } from './useSession';

export function LoginPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [mfaToken, setMfaToken] = useState<string | null>(null);
  const [code, setCode] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Prime the XSRF cookie so the login POST carries the X-XSRF-TOKEN header.
  useEffect(() => {
    void primeCsrf();
  }, []);

  async function finishSignIn() {
    await queryClient.invalidateQueries({ queryKey: sessionQueryKey });
    navigate('/dashboard', { replace: true });
  }

  async function submitPassword(event: FormEvent) {
    event.preventDefault();
    setError(null);
    setBusy(true);
    const { data, error: requestError } = await api.POST('/api/auth/login', {
      body: { email, password },
    });
    setBusy(false);

    if (requestError || !data) {
      setError('Invalid email or password.');
      return;
    }
    if (data.status === 'mfa-required') {
      setMfaToken(data.mfaToken);
      return;
    }
    await finishSignIn();
  }

  async function submitMfa(event: FormEvent) {
    event.preventDefault();
    if (!mfaToken) return;
    setError(null);
    setBusy(true);
    const { data, error: requestError } = await api.POST('/api/auth/mfa', {
      body: { mfaToken, code },
    });
    setBusy(false);

    if (requestError || !data) {
      setError('Invalid authentication code.');
      return;
    }
    await finishSignIn();
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
                Authentication code
                <Input
                  inputMode="numeric"
                  autoComplete="one-time-code"
                  value={code}
                  onChange={(e) => setCode(e.target.value)}
                  placeholder="123456"
                  autoFocus
                />
              </label>
              {error && (
                <span className="fs13" style={{ color: 'var(--neg)' }}>
                  {error}
                </span>
              )}
              <Button type="submit" variant="primary" disabled={busy}>
                {busy ? 'Verifying…' : 'Verify'}
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
                <span className="fs13" style={{ color: 'var(--neg)' }}>
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
