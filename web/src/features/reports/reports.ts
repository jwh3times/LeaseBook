import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { api, primeCsrf, type components } from '@/api';
import { num } from '@/lib/directory';

export type StatementView = components['schemas']['StatementView'];
export type StatementSectionView = components['schemas']['StatementSectionView'];
export type StatementLineView = components['schemas']['StatementLineView'];
export type FiduciaryPanel = components['schemas']['FiduciaryPanel'];
export type PmBrandingRow = components['schemas']['PmBrandingRow'];
export type ReconciliationSnapshotRow = components['schemas']['ReconciliationSnapshotRow'];
export type ReportDescriptor = components['schemas']['ReportDescriptor'];

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
      const { data, error } = await api.GET('/api/statements/{ownerId}', {
        params: {
          path: { ownerId },
          query: {
            basis: filters.basis,
            year: filters.year,
            month: filters.month,
            propertyId: filters.propertyId,
          },
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
      const { data, error } = await api.GET('/api/reports');
      if (error || !data) throw new Error('Failed to load report catalog');
      return data;
    },
  });
}

// Preview returns a generic JSON response; the schema marks content as never
// (the backend streams an anonymous shape). We fetch raw and type as unknown rows.
export interface PreviewResponse {
  columns: string[];
  rows: Record<string, unknown>[];
  totalRows: number;
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
      // The generated client marks the preview response content as `never` (schema says content?: never)
      // because the .NET endpoint returns an un-typed anonymous shape. Use a raw fetch so we can
      // parse the actual JSON body; MSW intercepts this correctly in the test environment.
      const params = new URLSearchParams();
      if (filters.year != null) params.set('year', String(filters.year));
      if (filters.month != null) params.set('month', String(filters.month));
      if (filters.asOf) params.set('asOf', filters.asOf);
      if (filters.propertyId) params.set('propertyId', filters.propertyId);
      if (filters.ownerId) params.set('ownerId', filters.ownerId);
      if (filters.bankAccountId) params.set('bankAccountId', filters.bankAccountId);

      const response = await fetch(
        `/api/reports/${encodeURIComponent(id)}/preview?${params.toString()}`,
        { credentials: 'include' },
      );
      if (!response.ok) throw new Error(`Preview failed (${response.status})`);
      const json = (await response.json()) as PreviewResponse;
      return json;
    },
  });
}

function buildFilterParams(filters: ReportFilters): string {
  const params = new URLSearchParams();
  if (filters.year != null) params.set('year', String(filters.year));
  if (filters.month != null) params.set('month', String(filters.month));
  if (filters.asOf) params.set('asOf', filters.asOf);
  if (filters.propertyId) params.set('propertyId', filters.propertyId);
  if (filters.ownerId) params.set('ownerId', filters.ownerId);
  if (filters.bankAccountId) params.set('bankAccountId', filters.bankAccountId);
  return params.toString();
}

// ---- Mutations ---------------------------------------------------------------

export interface ReportsError {
  code?: string;
  message: string;
}

interface ProblemBody {
  code?: string;
  detail?: string;
  title?: string;
}

function toReportsError(error: unknown, status: number): ReportsError {
  const body = (error ?? {}) as ProblemBody;
  return {
    code: body.code,
    message: body.detail ?? body.title ?? `Request failed (${status}).`,
  };
}

async function unwrapResponse(call: Promise<Response>): Promise<void> {
  const response = await call;
  if (!response.ok) {
    let body: unknown;
    try {
      body = await response.json();
    } catch {
      body = {};
    }
    throw toReportsError(body, response.status);
  }
}

export async function deliverStatement(
  ownerId: string,
  filters: StatementFilters,
  toEmail?: string,
): Promise<void> {
  await primeCsrf();
  const params = new URLSearchParams({
    basis: filters.basis,
    year: String(filters.year),
    month: String(filters.month),
  });
  if (filters.propertyId) params.set('propertyId', filters.propertyId);
  if (toEmail) params.set('toEmail', toEmail);
  await unwrapResponse(
    fetch(`/api/statements/${encodeURIComponent(ownerId)}/deliver?${params.toString()}`, {
      method: 'POST',
      credentials: 'include',
    }),
  );
}

/** Triggers a browser download for PDF or CSV. Uses authenticated fetch → blob → anchor. */
export async function downloadStatement(
  ownerId: string,
  filters: StatementFilters,
  format: 'pdf' | 'csv',
): Promise<void> {
  const params = new URLSearchParams({
    basis: filters.basis,
    year: String(filters.year),
    month: String(filters.month),
  });
  if (filters.propertyId) params.set('propertyId', filters.propertyId);
  const response = await fetch(
    `/api/statements/${encodeURIComponent(ownerId)}/${format}?${params.toString()}`,
    { credentials: 'include' },
  );
  if (!response.ok) throw new Error(`Download failed (${response.status})`);
  const blob = await response.blob();
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = `statement-${ownerId}-${filters.year}-${String(filters.month).padStart(2, '0')}.${format}`;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}

/** Triggers a browser download for a report's CSV export. */
export async function downloadReportCsv(id: string, filters: ReportFilters): Promise<void> {
  const response = await fetch(
    `/api/reports/${encodeURIComponent(id)}/csv?` + buildFilterParams(filters),
    { credentials: 'include' },
  );
  if (!response.ok) throw new Error(`Download failed (${response.status})`);
  const blob = await response.blob();
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
