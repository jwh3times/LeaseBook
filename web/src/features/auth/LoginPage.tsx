import { useEffect, useState, type FormEvent } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router';
import {
  asApiError,
  postApiAuthLogin,
  postApiAuthMfa,
  postApiAuthMfaRecovery,
  primeCsrf,
  unwrap,
  type ApiError,
} from '@/api';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { Button, Card, Input } from '@/design';
import { sessionQueryKey } from './useSession';

/**
 * Sign-in is the one surface that must not render the server's own explanation by default.
 * `AuthEndpoints` answers bad credentials, lockout and an unknown email with one generic code and
 * one generic message on purpose — login never reveals whether an email exists — so honouring the
 * error contract here the way every other surface does would be an account-enumeration regression.
 *
 * The distinction that matters is not *which* credential failed but **whether a credential was
 * judged at all**. When one was, nothing may be revealed. When the attempt never got that far there
 * is nothing to enumerate, the user's correct next action is different (start again, wait, or
 * contact support — not retype their password), and withholding the reason leaves the only operator
 * failure in the product with no reference to quote (#360).
 *
 * These three endpoints judge a credential at exactly one status: **401**. Everything else is the
 * request failing before or around that judgement — a validation 400 describing the fields the user
 * just submitted, `antiforgery_rejected`, a 5xx, a dropped connection — and none of those bodies can
 * carry account-specific information, so they render in full.
 *
 * Keying the generic branch on 401 rather than on a list of credential codes is what makes this
 * safe as the contract grows: a 401 code added later defaults to the generic copy, which is merely
 * unhelpful. The inverse — listing the credential codes and revealing everything else — would
 * default a new credential code to rendering the server's detail on the password step, which is the
 * enumeration leak itself. Same direction, and the same reasoning, as `isSessionExpired` (ADR-025).
 */
function isCredentialRejection(error: ApiError): boolean {
  // The one 401 that is not a judgement: the partial 2FA cookie expired between the two steps.
  return error.status === 401 && error.code !== 'mfa_session_expired';
}

/**
 * `ApiErrorNotice` renders `error.message`, and `unwrap` guarantees that is never empty — a
 * body-less response falls back to the neutral string passed below. So a shape that carries no
 * server explanation needs one substituted here; passing `ApiErrorNotice`'s `fallback` prop would
 * be dead code, because it is only consulted when `message` is falsy.
 */
function withDisplayMessage(error: ApiError): ApiError {
  if (error.status === 0) {
    return {
      ...error,
      message: 'Could not reach the server. Check your connection and try again.',
    };
  }
  if (error.status === 429) {
    return { ...error, message: 'Too many sign-in attempts. Wait a moment and try again.' };
  }
  return error;
}

export function LoginPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [mfaToken, setMfaToken] = useState<string | null>(null);
  const [useRecoveryCode, setUseRecoveryCode] = useState(false);
  const [code, setCode] = useState('');
  const [error, setError] = useState<ApiError | null>(null);
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
      // The fallback must stay neutral: `toApiError` uses it as `message` whenever the response
      // carried no body, and a bare 429 rendering "Invalid email or password." would be a lie.
      // The credential branch below supplies that copy itself.
      const data = await unwrap(
        postApiAuthLogin({ body: { email, password } }),
        'Sign-in could not be completed.',
      );
      setPassword('');
      if (data.status === 'mfa-required') {
        setMfaToken(data.mfaToken);
        return;
      }
      await finishSignIn();
    } catch (e) {
      setError(asApiError(e, 'Sign-in could not be completed.'));
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
        'Sign-in could not be completed.',
      );
      setCode('');
      await finishSignIn();
    } catch (e) {
      const failure = asApiError(e, 'Sign-in could not be completed.');
      // The partial session is gone, so the code field can no longer do anything: every Verify from
      // here re-issues the same 401. Return to the password step the copy points at, rather than
      // leaving the operator on a form whose only control cannot resolve its own error (#357, #360).
      if (failure.code === 'mfa_session_expired') {
        setMfaToken(null);
        setUseRecoveryCode(false);
        setCode('');
      }
      setError(failure);
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
              {error &&
                (isCredentialRejection(error) ? (
                  <span role="alert" className="fs13" style={{ color: 'var(--neg)' }}>
                    {useRecoveryCode ? 'Invalid recovery code.' : 'Invalid authentication code.'}
                  </span>
                ) : (
                  <ApiErrorNotice error={withDisplayMessage(error)} />
                ))}
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
              {error &&
                (isCredentialRejection(error) ? (
                  // Generic for a rejected credential, a lockout and an unknown email alike. The
                  // server's own detail must not reach this branch.
                  <span role="alert" className="fs13" style={{ color: 'var(--neg)' }}>
                    Invalid email or password.
                  </span>
                ) : (
                  <ApiErrorNotice error={withDisplayMessage(error)} />
                ))}
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
