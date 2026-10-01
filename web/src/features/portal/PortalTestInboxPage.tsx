import { useEffect, useState } from 'react';
import {
  asApiError,
  getApiPortalAccessTestInbox,
  unwrap,
  type ApiError,
  type PortalTestMessage,
} from '@/api';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { ErrorAction } from '@/components/ErrorAction';
import { Button, Card } from '@/design';

export function PortalTestInboxPage() {
  const [messages, setMessages] = useState<PortalTestMessage[] | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [revision, setRevision] = useState(0);
  const [busy, setBusy] = useState(true);
  useEffect(() => {
    let active = true;
    setBusy(true);
    setError(null);
    void unwrap(getApiPortalAccessTestInbox(), 'Could not load the test inbox.')
      .then((data) => {
        if (active) setMessages(data);
      })
      .catch((e) => {
        if (active) {
          setError(asApiError(e));
          setMessages(null);
        }
      })
      .finally(() => {
        if (active) setBusy(false);
      });
    return () => {
      active = false;
    };
  }, [revision]);
  return (
    <div className="col gap16">
      <h2>Portal test inbox</h2>
      <p>Development only. These invitations have not been emailed.</p>
      <p>
        To act as the recipient, copy the invitation link into a separate private browser session.
      </p>
      <Card pad>
        {error ? (
          <div className="col gap8">
            <ApiErrorNotice error={error} kind="read" />
            <ErrorAction
              error={error}
              onRetry={() => setRevision((value) => value + 1)}
              retrying={busy}
            />
          </div>
        ) : busy ? (
          <p role="status">Loading test inbox…</p>
        ) : (
          messages && (
            <div className="col gap12">
              {messages.length === 0 ? (
                <p>No pending invitations in the test inbox.</p>
              ) : (
                messages.map((message) => (
                  <a
                    key={message.invitationId}
                    href={message.acceptUrl}
                    referrerPolicy="no-referrer"
                    style={{ color: 'var(--text)' }}
                  >
                    Open invitation for {message.email}
                  </a>
                ))
              )}
            </div>
          )
        )}
        {!error && (
          <Button disabled={busy} onClick={() => setRevision((value) => value + 1)}>
            Refresh inbox
          </Button>
        )}
      </Card>
    </div>
  );
}
