import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import {
  getApiPortalOwnerStatements,
  getApiPortalOwnerStatementsByArtifactIdPdf,
  getApiPortalOwnerSummary,
  openDocument,
  unwrap,
  type OwnerPortalStatement,
  type OwnerPortalStatements,
  type OwnerPortalSummary,
} from '@/api';

export const ownerPortalSummaryKey = ['owner-portal', 'summary'] as const;
export const ownerPortalStatementsKey = ['owner-portal', 'statements'] as const;

// Owner money is read fresh on every visit and never kept once the page is gone: the server resolves
// the owner link on every request, and a cached answer would outlive a revoked link.
const PORTAL_READ = {
  retry: false,
  staleTime: 0,
  gcTime: 0,
  refetchOnWindowFocus: true,
} as const;

export function useOwnerPortalSummary(): UseQueryResult<OwnerPortalSummary> {
  return useQuery({
    queryKey: ownerPortalSummaryKey,
    queryFn: () => unwrap(getApiPortalOwnerSummary(), 'Unable to load your trust balance.'),
    ...PORTAL_READ,
  });
}

export function useOwnerPortalStatements(): UseQueryResult<OwnerPortalStatements> {
  return useQuery({
    queryKey: ownerPortalStatementsKey,
    queryFn: () => unwrap(getApiPortalOwnerStatements(), 'Unable to load your statements.'),
    ...PORTAL_READ,
  });
}

/** The problem code the host sends for an issued statement whose stored document is missing. */
export const STATEMENT_DOCUMENT_UNAVAILABLE = 'statement_document_unavailable';

export function statementPeriod(
  statement: Pick<OwnerPortalStatement, 'periodYear' | 'periodMonth'>,
) {
  const date = new Date(Number(statement.periodYear), Number(statement.periodMonth) - 1, 1);
  return date.toLocaleString('en-US', { month: 'long', year: 'numeric' });
}

/** Fetches one issued statement's PDF and opens it; throws an ApiError the page switches on. */
export function openStatementDocument(statement: OwnerPortalStatement): Promise<void> {
  const month = String(statement.periodMonth).padStart(2, '0');
  return openDocument(
    () =>
      getApiPortalOwnerStatementsByArtifactIdPdf({
        path: { artifactId: statement.id },
        parseAs: 'blob',
      }),
    `statement-${statement.periodYear}-${month}.pdf`,
    'This statement is not available.',
  );
}
