import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import {
  getApiReports,
  getApiReportsByIdCsv,
  getApiReportsByIdPreview,
  getApiReportsCompliancePack,
  getApiStatementsByOwnerId,
  getApiStatementsByOwnerIdCsv,
  getApiStatementsByOwnerIdPdf,
  postApiStatementsByOwnerIdDeliver,
  primeCsrf,
  type FiduciaryPanel,
  type PmBrandingRow,
  type ReconciliationSnapshotRow,
  type ReportDescriptor,
  type StatementLineView,
  type StatementSectionView,
  type StatementView,
} from '@/api';
import { num } from '@/lib/directory';
import { toApiError, type ApiError } from '@/lib/apiError';

export type {
  FiduciaryPanel,
  PmBrandingRow,
  ReconciliationSnapshotRow,
  ReportDescriptor,
  StatementLineView,
  StatementSectionView,
  StatementView,
};

// Re-export num for use in components (avoids a second import).
export { num };

// ---- Statement filters -------------------------------------------------------

export interface StatementFilters {
  basis: 'cash' | 'accrual';
  year: number;
  month: number;
  propertyId?: string;
}

export function currentPeriodFilters(): StatementFilters {
  const now = new Date();
  return { basis: 'cash', year: now.getFullYear(), month: now.getMonth() + 1 };
}

// ---- Report filters ----------------------------------------------------------

/**
 * The filters the report builder sends to the preview and CSV routes. These are exactly the query
 * parameters those routes bind — there is deliberately no `basis` here. Owner statements are
 * basis-aware and carry it on {@link StatementFilters}; the generic preview queries have no basis
 * dimension, so offering one would refetch and return identical figures (#229, #230).
 */
export interface ReportFilters {
  year?: number;
  month?: number;
  asOf?: string;
  propertyId?: string;
  ownerId?: string;
  bankAccountId?: string;
}

// ---- Query keys --------------------------------------------------------------

export const statementKey = (ownerId: string, filters: StatementFilters) =>
  ['statement', ownerId, filters] as const;

export const reportCatalogKey = () => ['reports'] as const;

export const reportPreviewKey = (id: string, filters: ReportFilters) =>
  ['report-preview', id, filters] as const;

// ---- Hooks -------------------------------------------------------------------

export function useStatement(
  ownerId: string,
  filters: StatementFilters,
): UseQueryResult<StatementView> {
  return useQuery({
    queryKey: statementKey(ownerId, filters),
    enabled: !!ownerId,
    queryFn: async () => {
      const { data, error } = await getApiStatementsByOwnerId({
        path: { ownerId },
        query: {
          basis: filters.basis,
          year: filters.year,
          month: filters.month,
          propertyId: filters.propertyId,
        },
      });
      if (error || !data) throw new Error('Failed to load owner statement');
      return data;
    },
  });
}

export function useReportCatalog(): UseQueryResult<ReportDescriptor[]> {
  return useQuery({
    queryKey: reportCatalogKey(),
    queryFn: async () => {
      const { data, error } = await getApiReports();
      if (error || !data) throw new Error('Failed to load report catalog');
      return data;
    },
  });
}

// Preview response shape — matches PreviewSpaResponse from the backend (now typed in OpenAPI).
export interface PreviewResponse {
  columns: string[];
  rows: Record<string, unknown>[];
  totalRows: number;
  /** Backend-supplied contextual message (e.g. redirect hint, no-data explanation). */
  message?: string | null;
}

export function useReportPreview(
  id: string,
  filters: ReportFilters,
  enabled = true,
): UseQueryResult<PreviewResponse> {
  return useQuery({
    queryKey: reportPreviewKey(id, filters),
    enabled: !!id && enabled,
    queryFn: async () => {
      // The preview endpoint is now annotated with Produces<PreviewSpaResponse> (WP-6/M6), so
      // the generated client types the response correctly. Use the typed api client directly.
      const { data, error } = await getApiReportsByIdPreview({
        path: { id },
        query: {
          year: filters.year,
          month: filters.month,
          asOf: filters.asOf,
          ownerId: filters.ownerId,
          propertyId: filters.propertyId,
          bankAccountId: filters.bankAccountId,
        },
      });
      if (error || !data) throw new Error(`Preview failed`);
      // The schema types rows as unknown[]; cast to the expected row shape for the preview table.
      return {
        columns: data.columns,
        rows: data.rows as Record<string, unknown>[],
        totalRows: Number(data.totalRows),
        message: data.message,
      };
    },
  });
}

// ---- Mutations ---------------------------------------------------------------

export type ReportsError = ApiError;
const toReportsError = toApiError;

interface ProblemBody {
  code?: string;
  detail?: string;
  title?: string;
  correlationId?: string;
}

export async function deliverStatement(
  ownerId: string,
  filters: StatementFilters,
  toEmail?: string,
): Promise<void> {
  await primeCsrf();
  const { error, response } = await postApiStatementsByOwnerIdDeliver({
    path: { ownerId },
    query: {
      basis: filters.basis,
      year: filters.year,
      month: filters.month,
      propertyId: filters.propertyId,
      toEmail,
    },
  });
  if (error || !response?.ok) throw toReportsError(error, response?.status ?? 0);
}

/** Triggers a browser download for PDF or CSV through the authenticated generated client. */
export async function downloadStatement(
  ownerId: string,
  filters: StatementFilters,
  format: 'pdf' | 'csv',
): Promise<void> {
  const options = {
    path: { ownerId },
    query: {
      basis: filters.basis,
      year: filters.year,
      month: filters.month,
      propertyId: filters.propertyId,
    },
    parseAs: 'blob' as const,
  };
  const result =
    format === 'pdf'
      ? await getApiStatementsByOwnerIdPdf(options)
      : await getApiStatementsByOwnerIdCsv(options);
  if (result.error || !(result.data instanceof Blob)) {
    throw toApiError(result.error, result.response?.status ?? 0);
  }
  const blob = result.data;
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = `statement-${ownerId}-${filters.year}-${String(filters.month).padStart(2, '0')}.${format}`;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}

// ---- Compliance pack (WP-8) --------------------------------------------------

/** A trust-account × from/to date range — the two inputs the compliance pack needs. */
export interface CompliancePackRange {
  bankAccountId: string;
  /** Inclusive period start, yyyy-MM-dd. */
  from: string;
  /** Inclusive period end, yyyy-MM-dd; its month must be reconciliation-locked. */
  to: string;
}

/**
 * Maps a failed compliance-pack response to a clear, human error. The RFC7807 problem body carries
 * the domain code in `title` (`period_not_closed`, `invalid_period`); we key off status + title so
 * the message never depends on color alone (WCAG 1.4.1) and reads sensibly for each failure.
 */
export function compliancePackError(body: ProblemBody, status: number): ReportsError {
  const code = body.code ?? body.title;
  const correlationId = body.correlationId;
  if (status === 422 || code === 'period_not_closed') {
    return {
      code: 'period_not_closed',
      message:
        "This period isn't closed yet — finalize the reconciliation for every month in the range first.",
      correlationId,
    };
  }
  if (status === 404) {
    return {
      code: 'bank_not_found',
      message: 'That trust account could not be found.',
      correlationId,
    };
  }
  if (status === 400 || code === 'invalid_period') {
    return {
      code: 'invalid_period',
      message: 'The start date must be on or before the end date.',
      correlationId,
    };
  }
  return {
    code,
    message: body.detail ?? body.title ?? `Download failed (${status}).`,
    correlationId,
  };
}

/**
 * Triggers a browser download of the trust compliance pack ZIP for one trust account and period
 * (authenticated client → blob → anchor click, matching downloadStatement / downloadReportCsv). On a
 * non-2xx it throws a {@link ReportsError} with a friendly, code-aware message (see
 * {@link compliancePackError}); the caller renders `.message` in a non-color-only alert.
 */
export async function downloadCompliancePack(
  bankAccountId: string,
  from: string,
  to: string,
): Promise<void> {
  const { data, error, response } = await getApiReportsCompliancePack({
    query: { bankAccountId, from, to },
    parseAs: 'blob',
  });
  if (error || !(data instanceof Blob)) {
    throw compliancePackError(error ?? {}, response?.status ?? 0);
  }
  const blob = data;
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = `compliance-pack-${from}-${to}.zip`;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}

/** Triggers a browser download for a report's CSV export. */
export async function downloadReportCsv(id: string, filters: ReportFilters): Promise<void> {
  const { data, error, response } = await getApiReportsByIdCsv({
    path: { id },
    query: {
      year: filters.year,
      month: filters.month,
      asOf: filters.asOf,
      propertyId: filters.propertyId,
      ownerId: filters.ownerId,
      bankAccountId: filters.bankAccountId,
    },
    parseAs: 'blob',
  });
  if (error || !(data instanceof Blob)) {
    throw toApiError(error, response?.status ?? 0);
  }
  const blob = data;
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = `report-${id}.csv`;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}

// Month label helper used in multiple components.
const MONTHS = [
  'January',
  'February',
  'March',
  'April',
  'May',
  'June',
  'July',
  'August',
  'September',
  'October',
  'November',
  'December',
];

export function monthLabel(year: number, month: number): string {
  return `${MONTHS[month - 1] ?? 'Unknown'} ${year}`;
}
