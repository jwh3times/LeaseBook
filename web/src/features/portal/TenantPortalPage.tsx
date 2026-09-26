import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router';
import { asApiError, getApiPortalTenantLedger, unwrap, type ApiError } from '@/api';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { QueryErrorState } from '@/components/QueryErrorState';
import { Badge, Button, Card, EmptyState, Money } from '@/design';
import { signOutCurrentSession } from '@/features/auth/signOut';

export function TenantPortalPage() {
  const queries = useQueryClient();
  const [signingOut, setSigningOut] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const ledger = useQuery({
    queryKey: ['resident-ledger'],
    queryFn: () => unwrap(getApiPortalTenantLedger(), 'Unable to load your rent ledger.'),
    retry: false,
    staleTime: 0,
    gcTime: 0,
    refetchOnWindowFocus: true,
  });
  async function signOut() {
    setSigningOut(true);
    setError(null);
    try {
      await signOutCurrentSession();
      queries.clear();
      window.location.assign('/login');
    } catch (e) {
      setError(asApiError(e, 'Unable to sign out.'));
    } finally {
      setSigningOut(false);
    }
  }
  return (
    <main className="pf-page col gap16">
      <h1>Tenant portal</h1>
      <nav aria-label="Tenant account" className="row gap16">
        <Link to="/account/security" style={{ color: 'var(--text)' }}>
          Account security
        </Link>
        <Button onClick={() => void signOut()} disabled={signingOut}>
          Sign out everywhere
        </Button>
      </nav>
      {error && <ApiErrorNotice error={error} fallback="Unable to sign out." />}
      {ledger.isError ? (
        <QueryErrorState
          query={ledger}
          title={
            asApiError(ledger.error).status === 403
              ? 'Resident access unavailable'
              : 'Couldn’t load your rent ledger'
          }
          fallback="Unable to load your rent ledger."
        />
      ) : ledger.isPending ? (
        <div role="status" className="pf-skeleton">
          Loading rent ledger…
        </div>
      ) : (
        <>
          <h2>{ledger.data.residentName}</h2>
          <Card>
            <h3>Rent ledger balance</h3>
            <Money value={Number(ledger.data.balance)} big />
            <p>
              This balance includes rent charges, credits, and prepayments. Security deposits are
              held separately.
            </p>
          </Card>
          {ledger.data.rows.length === 0 ? (
            <EmptyState icon="doc" title="No rent ledger activity yet" />
          ) : (
            <div style={{ overflowX: 'auto' }}>
              <table className="pf-table">
                <caption>Rent ledger activity</caption>
                <thead>
                  <tr>
                    <th scope="col">Date</th>
                    <th scope="col">Category</th>
                    <th scope="col">Status</th>
                    <th scope="col" className="num">
                      Charge
                    </th>
                    <th scope="col" className="num">
                      Payment / credit
                    </th>
                    <th scope="col" className="num">
                      Running balance
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {ledger.data.rows.map((row, index) => (
                    <tr key={index}>
                      <td>{row.date}</td>
                      <td>{row.category}</td>
                      <td>
                        {row.isReversal ? (
                          <Badge dot>Reversal</Badge>
                        ) : row.isVoided ? (
                          <Badge dot>Voided</Badge>
                        ) : (
                          'Posted'
                        )}
                      </td>
                      <td className="num">
                        <Money value={Number(row.charge)} />
                      </td>
                      <td className="num">
                        <Money value={Number(row.payment)} />
                      </td>
                      <td className="num">
                        <Money value={Number(row.balance)} />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </main>
  );
}
