import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import {
  download,
  getApiReports,
  getApiReportsByIdCsv,
  getApiReportsByIdPreview,
  getApiReportsCompliancePack,
  getApiStatementsByOwnerId,
  getApiStatementsByOwnerIdCsv,
  getApiStatementsByOwnerIdPdf,
  postApiStatementsByOwnerIdDeliver,
  toApiError,
  unwrap,
  type ApiError,
  type FiduciaryPanel,
  type PmBrandingRow,
  type ReconciliationSnapshotRow,
  type ReportDescriptor,
  type StatementLineView,
  type StatementSectionView,
  type StatementView,
} from '@/api';
import { num } from '@/lib/directory';

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
 * parameters those routes bind.
 *
 * `basis` is carried for `owner-bal` only, because it is the one preview where choosing a basis
 * changes the answer: `owner_equity` is credited `accrual` on RentCharged and `cash` on
 * PaymentReceived, so its Operating column genuinely differs. Every other preview is fixed-basis
 * for one of two reasons — its lines are all tagged `both` and read identically either way (bank
 * registers, held deposits, PM income), or the figure exists on one basis only (a receivable is
 * accrual by nature, which is why delinquency is accrual-only). The catalog's `filterControls`
 * decides where the control appears, so a basis chip cannot reappear on a report it would not
 * move (#230).
 */
export type ReportBasis = 'cash' | 'accrual';

export interface ReportFilters {
  year?: number;
  month?: number;
  asOf?: string;
  propertyId?: string;
  ownerId?: string;
  bankAccountId?: string;
  /**
   * Only meaningful on reports whose `filterControls` include 'basis' — today that is `owner-bal`
   * alone. Sending it elsewhere is harmless but changes nothing: no other preview reads an account
   * class that carries single-basis lines (#230).
   */
  basis?: ReportBasis;
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
      return unwrap(
        getApiStatementsByOwnerId({
          path: { ownerId },
          query: {
            basis: filters.basis,
            year: filters.year,
            month: filters.month,
            propertyId: filters.propertyId,
          },
        }),
        'Failed to load owner statement',
      );
    },
  });
}

export function useReportCatalog(): UseQueryResult<ReportDescriptor[]> {
  return useQuery({
    queryKey: reportCatalogKey(),
    queryFn: () => unwrap(getApiReports(), 'Failed to load report catalog'),
  });
}

// Preview response shape — matches PreviewSpaResponse from the backend (now typed in OpenAPI).
export interface PreviewResponse {
  columns: string[];
  rows: Record<string, unknown>[];
  totalRows: number;
  /** Backend-supplied contextual message (e.g. redirect hint, no-data explanation). */
  message?: string | null;
  /**
   * The basis the server actually computed on, or null when the report has no basis dimension.
   * Render this rather than local state: a label driven by the client's own toggle is exactly how
   * the builder used to claim a basis nothing had applied (#229).
   */
  basis?: string | null;
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
      const data = await unwrap(
        getApiReportsByIdPreview({
          path: { id },
          query: {
            year: filters.year,
            month: filters.month,
            asOf: filters.asOf,
            ownerId: filters.ownerId,
            propertyId: filters.propertyId,
            bankAccountId: filters.bankAccountId,
            basis: filters.basis,
          },
        }),
        'Preview failed',
      );
      // The schema types rows as unknown[]; cast to the expected row shape for the preview table.
      return {
        columns: data.columns,
        rows: data.rows as Record<string, unknown>[],
        totalRows: Number(data.totalRows),
        message: data.message,
        basis: data.basis,
      };
    },
  });
}

// ---- Mutations ---------------------------------------------------------------

export type ReportsError = ApiError;

export async function deliverStatement(
  ownerId: string,
  filters: StatementFilters,
  toEmail?: string,
): Promise<void> {
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
  if (error || !response?.ok) {
    throw toApiError(error, response?.status ?? 0, 'Failed to deliver the statement');
  }
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
  await download(
    () =>
      format === 'pdf'
        ? getApiStatementsByOwnerIdPdf(options)
        : getApiStatementsByOwnerIdCsv(options),
    `statement-${ownerId}-${filters.year}-${String(filters.month).padStart(2, '0')}.${format}`,
    'Failed to download the statement',
  );
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
 * Triggers a browser download of the trust compliance pack ZIP for one trust account and period.
 * On a non-2xx it throws a {@link ReportsError} carrying the server's `code`, `status` and
 * `correlationId`; `CompliancePackPanel` turns those into the operator-facing copy.
 *
 * The status-to-copy map used to live here, and was the last bespoke survivor of ADR-025's five-way
 * consolidation. It sat in the wrong layer: four other surfaces already key friendly wording off
 * `err.code` in the component, and report-specific wording does not belong in a transport module.
 */
export async function downloadCompliancePack(
  bankAccountId: string,
  from: string,
  to: string,
): Promise<void> {
  await download(
    () => getApiReportsCompliancePack({ query: { bankAccountId, from, to }, parseAs: 'blob' }),
    `compliance-pack-${from}-${to}.zip`,
    'Download failed.',
  );
}

/** Triggers a browser download for a report's CSV export. */
export async function downloadReportCsv(id: string, filters: ReportFilters): Promise<void> {
  await download(
    () =>
      getApiReportsByIdCsv({
        path: { id },
        query: {
          year: filters.year,
          month: filters.month,
          asOf: filters.asOf,
          propertyId: filters.propertyId,
          ownerId: filters.ownerId,
          bankAccountId: filters.bankAccountId,
          // Same filters as the preview: an export that disagrees with the screen is worse than
          // no export.
          basis: filters.basis,
        },
        parseAs: 'blob',
      }),
    `report-${id}.csv`,
    'Failed to export the report',
  );
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
