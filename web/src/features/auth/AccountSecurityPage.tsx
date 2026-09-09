import { useState, type FormEvent } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router';
import QRCode from 'qrcode';
import {
  postApiAuthMfaEnroll,
  postApiAuthMfaEnrollConfirm,
  postApiAuthChangePassword,
  postApiAuthLogout,
  unwrap,
  asApiError,
  type EnrollResponse,
} from '@/api';
import { Button, Card, Input } from '@/design';
import { clearRecent } from '@/features/palette/recent';
import { sessionQueryKey, useSession } from './useSession';

/** Sensitive setup values stay in component memory, never persistent storage or query caches. */
export function AccountSecurityPage() {
  const { data: session } = useSession();
  const queries = useQueryClient();
  const [enrollment, setEnrollment] = useState<EnrollResponse | null>(null);
  const [qr, setQr] = useState<string | null>(null);
  const [code, setCode] = useState('');
  const [recovery, setRecovery] = useState<string[] | null>(null);
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function run(action: () => Promise<void>) {
    setError(null);
    setMessage(null);
    setBusy(true);
    try {
      await action();
    } catch (e) {
      setError(asApiError(e, 'Unable to update account security.').message);
    } finally {
      setBusy(false);
    }
  }
  async function startEnrollment() {
    await run(async () => {
      const result = await unwrap(postApiAuthMfaEnroll(), 'Unable to start enrollment.');
      setEnrollment(result);
      // QR encoding happens locally; no authenticator secret leaves the browser for rendering.
      setQr(await QRCode.toDataURL(result.otpauthUri, { width: 220, margin: 2 }));
    });
  }
  async function confirm(event: FormEvent) {
    event.preventDefault();
    await run(async () => {
      const result = await unwrap(
        postApiAuthMfaEnrollConfirm({ body: { code } }),
        'Unable to confirm the authentication code.',
      );
      setRecovery(result.codes);
      setEnrollment(null);
      setQr(null);
      setCode('');
      await queries.invalidateQueries({ queryKey: sessionQueryKey });
    });
  }
  async function changePassword(event: FormEvent) {
    event.preventDefault();
    await run(async () => {
      await unwrap(
        postApiAuthChangePassword({ body: { currentPassword, newPassword } }),
        'Unable to change password.',
        { allowNoContent: true },
      );
      setCurrentPassword('');
      setNewPassword('');
      setMessage('Password changed. Other sessions have been signed out.');
    });
  }
  async function signOut() {
    await run(async () => {
      await unwrap(postApiAuthLogout(), 'Unable to sign out.', { allowNoContent: true });
      clearRecent();
      queries.clear();
      window.location.assign('/login');
    });
  }
  return (
    <main className="pf-page col gap16" style={{ maxWidth: 620, margin: '0 auto' }}>
      <h1>Account security</h1>
      <p>
        {session?.mfaEnrollmentRequired
          ? 'Set up an authenticator to continue to LeaseBook.'
          : 'Manage your sign-in security.'}
      </p>
      {error && <p role="alert">{error}</p>}
      {message && <p role="status">{message}</p>}
      <Card pad>
        <h2>Authenticator</h2>
        {recovery ? (
          <div className="col gap12">
            <h3>Save your recovery codes</h3>
            <p>
              Store these in a password manager or another secure location. Each code works once if
              you lose your authenticator. They will not be shown again.
            </p>
            <ul aria-label="Recovery codes">
              {recovery.map((value) => (
                <li key={value}>
                  <code>{value}</code>
                </li>
              ))}
            </ul>
            <Button onClick={() => setRecovery(null)}>I have saved my recovery codes</Button>
          </div>
        ) : session?.mfaEnabled ? (
          <p>Authenticator enabled.</p>
        ) : enrollment ? (
          <form className="col gap12" onSubmit={confirm}>
            <p>Scan this QR code in your authenticator app, or enter the setup key manually.</p>
            {qr && <img src={qr} alt="Authenticator setup QR code" width={220} height={220} />}
            <label className="col gap6">
              Setup key
              <Input value={enrollment.secret} readOnly />
            </label>
            <label className="col gap6">
              Authentication code
              <Input
                value={code}
                onChange={(e) => setCode(e.target.value)}
                inputMode="numeric"
                autoComplete="one-time-code"
                pattern="[0-9]{6}"
                maxLength={6}
                required
              />
            </label>
            <Button type="submit" disabled={busy}>
              {busy ? 'Confirming…' : 'Confirm authenticator'}
            </Button>
          </form>
        ) : (
          <Button onClick={() => void startEnrollment()} disabled={busy}>
            {busy ? 'Preparing…' : 'Set up authenticator'}
          </Button>
        )}
      </Card>
      <Card pad>
        <h2>Change password</h2>
        <form className="col gap12" onSubmit={changePassword}>
          <label className="col gap6">
            Current password
            <Input
              type="password"
              autoComplete="current-password"
              value={currentPassword}
              onChange={(e) => setCurrentPassword(e.target.value)}
              required
            />
          </label>
          <label className="col gap6">
            New password
            <Input
              type="password"
              autoComplete="new-password"
              value={newPassword}
              onChange={(e) => setNewPassword(e.target.value)}
              minLength={12}
              required
            />
          </label>
          <p>
            Use at least 12 characters with upper and lower case letters, a number, and a symbol.
          </p>
          <Button type="submit" disabled={busy}>
            Change password
          </Button>
        </form>
      </Card>
      {!session?.mfaEnrollmentRequired && !recovery && (
        <Link to="/dashboard" style={{ color: 'var(--text)' }}>
          Continue to LeaseBook
        </Link>
      )}
      <Button onClick={() => void signOut()} disabled={busy}>
        Sign out
      </Button>
    </main>
  );
}
