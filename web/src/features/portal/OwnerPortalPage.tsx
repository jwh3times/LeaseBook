import { useState } from 'react';
import { useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import { Link } from 'react-router';
import {
  asApiError,
  isNotFound,
  isSessionExpired,
  type ApiError,
  type OwnerPortalActivityRow,
  type OwnerPortalDisbursement,
  type OwnerPortalStatement,
  type OwnerPortalSummary,
} from '@/api';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { ErrorAction } from '@/components/ErrorAction';
import { QueryErrorState } from '@/components/QueryErrorState';
import { Badge, Button, Card, EmptyState, Money } from '@/design';
import { signOutCurrentSession } from '@/features/auth/signOut';
import {
  openStatementDocument,
  STATEMENT_DOCUMENT_UNAVAILABLE,
  statementPeriod,
  useOwnerPortalStatements,
  useOwnerPortalSummary,
} from './ownerPortal';

function basisLabel(basis: string | null): string {
  return basis === 'cash' ? 'Cash' : basis === 'accrual' ? 'Accrual' : (basis ?? 'Not recorded');
}

function EntryStatus({ isVoided, isReversal }: { isVoided: boolean; isReversal: boolean }) {
  return isReversal ? (
    <Badge dot>Reversal</Badge>
  ) : isVoided ? (
    <Badge dot>Voided</Badge>
  ) : (
    <>Posted</>
  );
}

/**
 * A 403 means the server found no active owner link for this sign-in, and an expired session makes
 * every read fail the same way. Either answers for the whole page, so it is reported once rather
 * than once per section.
 */
function pageBlockingFailure(queries: UseQueryResult<unknown>[]) {
  return queries.find(
    (q) => q.isError && (asApiError(q.error).status === 403 || isSessionExpired(q.error)),
  );
}

export function OwnerPortalPage() {
  const queries = useQueryClient();
  const summary = useOwnerPortalSummary();
  const statements = useOwnerPortalStatements();
  const [signingOut, setSigningOut] = useState(false);
  const [signOutError, setSignOutError] = useState<ApiError | null>(null);

  async function signOut() {
    setSigningOut(true);
    setSignOutError(null);
    try {
      await signOutCurrentSession();
      queries.clear();
      window.location.assign('/login');
    } catch (e) {
      setSignOutError(asApiError(e, 'Unable to sign out.'));
    } finally {
      setSigningOut(false);
    }
  }

  const blocking = pageBlockingFailure([summary, statements]);

  return (
    <main className="pf-page col gap16">
      <h1>Owner portal</h1>
      <nav aria-label="Owner account" className="row gap16">
        <Link to="/account/security" style={{ color: 'var(--text)' }}>
          Account security
        </Link>
        <Button onClick={() => void signOut()} disabled={signingOut}>
          Sign out everywhere
        </Button>
      </nav>
      {signOutError && <ApiErrorNotice error={signOutError} fallback="Unable to sign out." />}
      {blocking ? (
        <QueryErrorState
          query={blocking}
          title={
            asApiError(blocking.error).status === 403
              ? 'Owner access unavailable'
              : 'Couldn’t load your owner portal'
          }
          fallback="Your owner portal is not available for this account."
          onRetry={() => {
            void summary.refetch();
            void statements.refetch();
          }}
        />
      ) : (
        <>
          <BalanceRegion summary={summary} />
          <StatementsSection statements={statements} />
          {summary.isSuccess && (
            <>
              <DisbursementsSection disbursements={summary.data.disbursements} />
              <ActivitySection activity={summary.data.activity} />
            </>
          )}
        </>
      )}
    </main>
  );
}

function BalanceRegion({ summary }: { summary: UseQueryResult<OwnerPortalSummary> }) {
  if (summary.isError) {
    return (
      <QueryErrorState
        query={summary}
        title="Couldn’t load your trust balance"
        fallback="Unable to load your trust balance."
      />
    );
  }
  if (summary.isPending) {
    return (
      <div role="status" className="pf-skeleton">
        Loading your trust balance…
      </div>
    );
  }
  return (
    <>
      <h2>{summary.data.ownerName}</h2>
      <Card pad>
        <h3>Balance held in trust</h3>
        <Money value={Number(summary.data.balance)} big />
        <p>
          {basisLabel(summary.data.basis)} basis. This is your money held in your property manager’s
          trust account. Tenant security deposits are held separately and are not included.
        </p>
      </Card>
    </>
  );
}

function StatementsSection({
  statements,
}: {
  statements: UseQueryResult<{ statements: OwnerPortalStatement[] }>;
}) {
  const [opening, setOpening] = useState<string | null>(null);
  const [failure, setFailure] = useState<{
    statement: OwnerPortalStatement;
    error: ApiError;
  } | null>(null);

  async function open(statement: OwnerPortalStatement) {
    setOpening(statement.id);
    setFailure(null);
    try {
      await openStatementDocument(statement);
    } catch (e) {
      setFailure({ statement, error: asApiError(e, 'Unable to open the statement.') });
    } finally {
      setOpening(null);
    }
  }

  return (
    <section aria-labelledby="owner-statements" className="col gap8">
      <h2 id="owner-statements">Statements</h2>
      {statements.isError ? (
        <QueryErrorState
          query={statements}
          title="Couldn’t load your statements"
          fallback="Unable to load your statements."
        />
      ) : statements.isPending ? (
        <div role="status" className="pf-skeleton">
          Loading statements…
        </div>
      ) : statements.data.statements.length === 0 ? (
        <EmptyState
          icon="doc"
          title="No statements issued yet"
          description="Statements appear here once your property manager issues them."
        />
      ) : (
        <>
          <div style={{ overflowX: 'auto' }}>
            <table className="pf-table">
              <caption>Issued statements, newest period first</caption>
              <thead>
                <tr>
                  <th scope="col">Period</th>
                  <th scope="col">Scope</th>
                  <th scope="col">Basis</th>
                  <th scope="col" className="num">
                    Ending balance
                  </th>
                  <th scope="col">Issued</th>
                  <th scope="col">Document</th>
                </tr>
              </thead>
              <tbody>
                {statements.data.statements.map((statement) => {
                  const period = statementPeriod(statement);
                  const issued = statement.issuedAt.slice(0, 10);
                  return (
                    <tr key={statement.id}>
                      <td>{period}</td>
                      <td>{statement.scope}</td>
                      <td>{basisLabel(statement.basis)}</td>
                      <td className="num">
                        {statement.endingBalance === null ? (
                          'Not recorded'
                        ) : (
                          <Money value={Number(statement.endingBalance)} />
                        )}
                      </td>
                      <td>{issued}</td>
                      <td>
                        <Button
                          size="sm"
                          icon="doc"
                          aria-label={`Open PDF: ${period} statement, ${statement.scope}, issued ${issued}`}
                          disabled={opening !== null}
                          onClick={() => void open(statement)}
                        >
                          {opening === statement.id ? 'Opening…' : 'Open PDF'}
                        </Button>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          {failure && (
            <DocumentFailure
              failure={failure}
              retrying={opening !== null}
              onRetry={() => void open(failure.statement)}
            />
          )}
        </>
      )}
    </section>
  );
}

/**
 * Switches on the problem code, not the status: `statement_document_unavailable` is the one failure
 * that means "issued, but the stored document is missing", and retrying it cannot help. A 404 means
 * the statement is gone for this account. Everything else is an ordinary failed read.
 */
function DocumentFailure({
  failure,
  retrying,
  onRetry,
}: {
  failure: { statement: OwnerPortalStatement; error: ApiError };
  retrying: boolean;
  onRetry: () => void;
}) {
  const { statement, error } = failure;
  const unavailable = error.code === STATEMENT_DOCUMENT_UNAVAILABLE;
  const gone = !unavailable && isNotFound(error);
  const label = `${statementPeriod(statement)} statement, ${statement.scope}`;
  return (
    <div className="col gap6">
      <strong>
        {unavailable ? `Statement document unavailable — ${label}` : `Couldn’t open the ${label}`}
      </strong>
      <ApiErrorNotice error={error} fallback="Unable to open the statement." kind="read" />
      {!unavailable && !gone && <ErrorAction error={error} onRetry={onRetry} retrying={retrying} />}
    </div>
  );
}

function DisbursementsSection({ disbursements }: { disbursements: OwnerPortalDisbursement[] }) {
  return (
    <section aria-labelledby="owner-disbursements" className="col gap8">
      <h2 id="owner-disbursements">Disbursements</h2>
      {disbursements.length === 0 ? (
        <EmptyState icon="doc" title="No disbursements yet" />
      ) : (
        <div style={{ overflowX: 'auto' }}>
          <table className="pf-table">
            <caption>Money paid out to you</caption>
            <thead>
              <tr>
                <th scope="col">Date</th>
                <th scope="col">Status</th>
                <th scope="col" className="num">
                  Amount
                </th>
              </tr>
            </thead>
            <tbody>
              {disbursements.map((row, index) => (
                <tr key={index}>
                  <td>{row.date}</td>
                  <td>
                    <EntryStatus isVoided={row.isVoided} isReversal={row.isReversal} />
                  </td>
                  <td className="num">
                    <Money value={Number(row.amount)} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

function ActivitySection({ activity }: { activity: OwnerPortalActivityRow[] }) {
  return (
    <section aria-labelledby="owner-activity" className="col gap8">
      <h2 id="owner-activity">Activity</h2>
      {activity.length === 0 ? (
        <EmptyState icon="doc" title="No trust activity yet" />
      ) : (
        <div style={{ overflowX: 'auto' }}>
          <table className="pf-table">
            <caption>Trust activity</caption>
            <thead>
              <tr>
                <th scope="col">Date</th>
                <th scope="col">Category</th>
                <th scope="col">Property</th>
                <th scope="col">Status</th>
                <th scope="col" className="num">
                  Amount
                </th>
                <th scope="col" className="num">
                  Running balance
                </th>
              </tr>
            </thead>
            <tbody>
              {activity.map((row, index) => (
                <tr key={index}>
                  <td>{row.date}</td>
                  <td>{row.category}</td>
                  <td>{row.propertyAddress ?? 'Not property-specific'}</td>
                  <td>
                    <EntryStatus isVoided={row.isVoided} isReversal={row.isReversal} />
                  </td>
                  <td className="num">
                    <Money value={Number(row.amount)} />
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
    </section>
  );
}
