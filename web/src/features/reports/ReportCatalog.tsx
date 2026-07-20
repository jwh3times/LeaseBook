import { useState } from 'react';
import { Badge, Button, Card, CardHeader, EmptyState, Icon } from '@/design';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { asApiError } from '@/lib/apiError';
import { useOwners, useProperties } from '@/lib/directory';
import { useBankBalances } from '@/features/banking/banking';
import { useSession } from '@/features/auth/useSession';
import {
  downloadReportCsv,
  type ReportDescriptor,
  type ReportFilters,
  type ReportsError,
  useReportCatalog,
  useReportPreview,
} from './reports';
import { ReportPreviewTable } from './ReportPreview';
import { CompliancePackPanel } from './CompliancePackPanel';
import { FilterChipBuilder, SelectChip, type SelectChipOption } from './chips';

// The compliance pack is a PMAdmin-only ZIP export (it carries the audit-log extract), so it is
// gated out of the catalog for non-admins and rendered by its own panel rather than the generic
// preview/CSV builder.
const COMPLIANCE_PACK_ID = 'compliance-pack';

// ---- Category tabs -----------------------------------------------------------

const ALL_CAT = 'All';

interface CatTabsProps {
  categories: string[];
  active: string;
  onChange: (cat: string) => void;
}

function CatTabs({ categories, active, onChange }: CatTabsProps) {
  return (
    <div className="pf-cat-tabs" role="tablist" aria-label="Report categories">
      {[ALL_CAT, ...categories].map((cat) => (
        <button
          key={cat}
          role="tab"
          aria-selected={active === cat}
          className={`pf-cat-tab${active === cat ? ' active' : ''}`}
          onClick={() => onChange(cat)}
        >
          {cat}
        </button>
      ))}
    </div>
  );
}

// ---- Report card -------------------------------------------------------------

interface ReportCardProps {
  report: ReportDescriptor;
  active: boolean;
  onSelect: () => void;
}

function ReportCard({ report, active, onSelect }: ReportCardProps) {
  const iconName =
    (report.icon as string) in
    // A safe fallback: if the icon name doesn't exist, use 'doc'
    {
      owners: true,
      dashboard: true,
      doc: true,
      bank: true,
      wallet: true,
      building: true,
      clock: true,
      reports: true,
      tenants: true,
    }
      ? (report.icon as import('@/design').IconName)
      : 'doc';

  return (
    <button
      className={`pf-report-card${active ? ' active' : ''}`}
      aria-pressed={active}
      onClick={onSelect}
    >
      <div className="pf-report-ic" aria-hidden="true">
        <Icon name={iconName} size={18} />
      </div>
      <div className="col" style={{ flex: 1, alignItems: 'flex-start', minWidth: 0, gap: 2 }}>
        <div className="row gap6">
          <span className="fw6 fs13">{report.name}</span>
          {report.favorite && (
            <Badge tone="accent" soft>
              ★
            </Badge>
          )}
        </div>
        <span className="t3 fs12" style={{ textAlign: 'left' }}>
          {report.description}
        </span>
      </div>
    </button>
  );
}

// ---- Period helpers ----------------------------------------------------------

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

function periodLabel(year: number, month: number): string {
  return `${MONTHS[month - 1] ?? 'Month'} ${year}`;
}

// ---- Basis toggle -----------------------------------------------------------

type Basis = 'cash' | 'accrual';

interface BasisToggleProps {
  basis: Basis;
  onChange: (b: Basis) => void;
}

function BasisToggle({ basis, onChange }: BasisToggleProps) {
  return (
    <div className="pf-basis-toggle" aria-label="Accounting basis">
      <span className="pf-fchip-label" style={{ marginRight: 4 }}>
        Basis
      </span>
      {(['cash', 'accrual'] as const).map((b) => (
        <button
          key={b}
          className={`pf-basis-btn${basis === b ? ' active' : ''}`}
          aria-pressed={basis === b}
          onClick={() => onChange(b)}
          type="button"
        >
          {b === 'cash' ? 'Cash' : 'Accrual'}
        </button>
      ))}
    </div>
  );
}

// ---- Builder panel ----------------------------------------------------------

// Reports whose preview supports a basis toggle (cash/accrual).
const BASIS_SENSITIVE = ['owner-stmt', 'trust-ledger', 'owner-bal'];

interface BuilderPanelProps {
  report: ReportDescriptor;
}

const CURRENT_YEAR = new Date().getFullYear();
const CURRENT_MONTH = new Date().getMonth() + 1;

function BuilderPanel({ report }: BuilderPanelProps) {
  const [year, setYear] = useState(CURRENT_YEAR);
  const [month, setMonth] = useState(CURRENT_MONTH);
  const [basis, setBasis] = useState<Basis>('cash');
  const [propertyId, setPropertyId] = useState<string | null>(null);
  const [ownerId, setOwnerId] = useState<string | null>(null);
  const [bankAccountId, setBankAccountId] = useState<string | null>(null);
  const [downloadError, setDownloadError] = useState<ReportsError | null>(null);
  const [periodOpen, setPeriodOpen] = useState(false);

  const af = (report.acceptedFilters as string[] | undefined) ?? [];
  const showBasisToggle = BASIS_SENSITIVE.includes(report.id);
  const showProperty = af.includes('propertyId');
  const showOwner = af.includes('ownerId');
  const showBank = af.includes('bankAccountId');

  // Only fetch lists when the corresponding filter chip is shown for the selected report.
  const ownersQuery = useOwners({ enabled: showOwner });
  const propertiesQuery = useProperties({ enabled: showProperty });
  const banksQuery = useBankBalances({ enabled: showBank });

  const ownerOptions: SelectChipOption[] = (ownersQuery.data?.items ?? []).map((o) => ({
    id: o.id,
    label: o.name,
  }));
  const propertyOptions: SelectChipOption[] = (propertiesQuery.data?.items ?? []).map((p) => ({
    id: p.id,
    label: p.address,
  }));
  const bankOptions: SelectChipOption[] = (banksQuery.data ?? []).map((b) => ({
    id: b.bankAccountId,
    label: b.name,
  }));

  const filters: ReportFilters = {
    year,
    month,
    ...(showBasisToggle ? { basis } : {}),
    ...(showProperty && propertyId ? { propertyId } : {}),
    ...(showOwner && ownerId ? { ownerId } : {}),
    ...(showBank && bankAccountId ? { bankAccountId } : {}),
  };

  const iconName =
    (report.icon as string) in
    {
      owners: true,
      dashboard: true,
      doc: true,
      bank: true,
      wallet: true,
      building: true,
      clock: true,
      reports: true,
      tenants: true,
    }
      ? (report.icon as import('@/design').IconName)
      : 'doc';

  const preview = useReportPreview(report.id, filters);

  const handleCsv = async () => {
    setDownloadError(null);
    try {
      await downloadReportCsv(report.id, filters);
    } catch (e) {
      setDownloadError(asApiError(e, 'Download failed'));
    }
  };

  const selectedOwnerLabel = ownerId
    ? (ownerOptions.find((o) => o.id === ownerId)?.label ?? 'Selected')
    : 'All';
  const selectedPropertyLabel = propertyId
    ? (propertyOptions.find((p) => p.id === propertyId)?.label ?? 'Selected')
    : 'All';
  const selectedBankLabel = bankAccountId
    ? (bankOptions.find((b) => b.id === bankAccountId)?.label ?? 'Selected')
    : 'All';

  return (
    <Card className="pf-builder">
      <CardHeader
        title={
          <div className="row gap10">
            <div className="pf-report-ic" aria-hidden="true">
              <Icon name={iconName} size={18} />
            </div>
            <div>
              <span>{report.name}</span>
              <div className="sub">{report.category} report</div>
            </div>
          </div>
        }
        actions={
          <>
            <Button
              icon="download"
              variant="primary"
              onClick={() => void handleCsv()}
              disabled={preview.isPending}
            >
              Export CSV
            </Button>
          </>
        }
      />

      {/* Filters strip */}
      <div className="pf-builder-filters" role="group" aria-label="Report filters">
        {/* Period chip */}
        <div className="pf-filter-wrap">
          <FilterChipBuilder
            label="Period"
            value={periodLabel(year, month)}
            active={periodOpen}
            onClick={() => setPeriodOpen((v) => !v)}
          />
          {periodOpen && (
            <div className="pf-filter-popover" role="dialog" aria-label="Select period">
              <div className="pf-period-label">Year</div>
              <div className="row gap6">
                {[CURRENT_YEAR, CURRENT_YEAR - 1, CURRENT_YEAR - 2].map((y) => (
                  <button
                    key={y}
                    className={`pf-basis-btn${year === y ? ' active' : ''}`}
                    onClick={() => setYear(y)}
                    aria-pressed={year === y}
                    type="button"
                  >
                    {y}
                  </button>
                ))}
              </div>
              <div className="pf-period-label" style={{ marginTop: 10 }}>
                Month
              </div>
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 6 }}>
                {MONTHS.map((m, i) => (
                  <button
                    key={m}
                    className={`pf-basis-btn${month === i + 1 ? ' active' : ''}`}
                    onClick={() => {
                      setMonth(i + 1);
                      setPeriodOpen(false);
                    }}
                    aria-pressed={month === i + 1}
                    type="button"
                  >
                    {m}
                  </button>
                ))}
              </div>
              <div style={{ marginTop: 10, textAlign: 'right' }}>
                <Button variant="primary" onClick={() => setPeriodOpen(false)}>
                  Done
                </Button>
              </div>
            </div>
          )}
        </div>

        {/* Owner chip — shown when report.acceptedFilters includes 'ownerId' */}
        {showOwner && (
          <SelectChip
            label="Owner"
            value={selectedOwnerLabel}
            options={ownerOptions}
            loading={ownersQuery.isPending}
            onSelect={setOwnerId}
          />
        )}

        {/* Property chip — shown when report.acceptedFilters includes 'propertyId' */}
        {showProperty && (
          <SelectChip
            label="Property"
            value={selectedPropertyLabel}
            options={propertyOptions}
            loading={propertiesQuery.isPending}
            onSelect={setPropertyId}
          />
        )}

        {/* Bank chip — shown when report.acceptedFilters includes 'bankAccountId' */}
        {showBank && (
          <SelectChip
            label="Bank"
            value={selectedBankLabel}
            options={bankOptions}
            loading={banksQuery.isPending}
            onSelect={setBankAccountId}
          />
        )}
      </div>

      {/* Basis toggle (for applicable reports) */}
      {showBasisToggle && <BasisToggle basis={basis} onChange={setBasis} />}

      <ApiErrorNotice error={downloadError} style={{ padding: '8px var(--card-pad)' }} />

      {/* Preview */}
      <div className="pf-builder-preview">
        <div className="pf-preview-bar">
          <span className="pf-eyebrow">Live preview</span>
          <span className="t3 fs12">Updates as you change filters</span>
        </div>

        {preview.isPending ? (
          <div className="pf-pad col gap8">
            {[0, 1, 2, 3, 4].map((i) => (
              <div key={i} className="pf-skeleton" style={{ height: 18 }} />
            ))}
          </div>
        ) : preview.isError ? (
          <div className="pf-pad">
            <EmptyState
              icon="alert"
              title="Preview failed"
              description="Could not load report data. Please retry."
            />
          </div>
        ) : preview.data ? (
          <div className="pf-preview-scroll">
            <ReportPreviewTable preview={preview.data} />
          </div>
        ) : null}
      </div>
    </Card>
  );
}

// ---- ReportCatalog page -----------------------------------------------------

export function ReportCatalog() {
  const catalog = useReportCatalog();
  const { data: session } = useSession();
  const isAdmin = session?.role === 'PMAdmin';
  const [activeCat, setActiveCat] = useState(ALL_CAT);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [search, setSearch] = useState('');

  // The compliance pack is PMAdmin-only; hide its card entirely for non-admins.
  const allReports = (catalog.data ?? []).filter((r) => r.id !== COMPLIANCE_PACK_ID || isAdmin);

  // Derive unique categories from the catalog.
  const categories = Array.from(new Set(allReports.map((r) => r.category)));

  const filtered = allReports.filter((r) => {
    if (activeCat !== ALL_CAT && r.category !== activeCat) return false;
    if (search.trim()) {
      const q = search.trim().toLowerCase();
      return r.name.toLowerCase().includes(q) || r.description.toLowerCase().includes(q);
    }
    return true;
  });

  // Auto-select first report when data loads or category changes.
  const effectiveSelectedId =
    selectedId && filtered.some((r) => r.id === selectedId)
      ? selectedId
      : (filtered[0]?.id ?? null);

  const selectedReport = allReports.find((r) => r.id === effectiveSelectedId) ?? null;

  if (catalog.isPending) {
    return (
      <div className="pf-fade">
        <div className="pf-pagehd">
          <div>
            <h2>Reports</h2>
          </div>
        </div>
        <div style={{ display: 'grid', gridTemplateColumns: '360px 1fr', gap: 'var(--gap)' }}>
          <div className="col gap8">
            {[0, 1, 2, 3].map((i) => (
              <Card key={i} pad>
                <div className="pf-skeleton" style={{ height: 52 }} />
              </Card>
            ))}
          </div>
          <Card pad>
            <div className="pf-skeleton" style={{ height: 200 }} />
          </Card>
        </div>
      </div>
    );
  }

  if (catalog.isError) {
    return (
      <div className="pf-fade">
        <div className="pf-pagehd">
          <div>
            <h2>Reports</h2>
          </div>
        </div>
        <Card pad>
          <EmptyState
            icon="alert"
            title="Couldn't load reports"
            description="Please retry in a moment."
          />
        </Card>
      </div>
    );
  }

  if (allReports.length === 0) {
    return (
      <div className="pf-fade">
        <div className="pf-pagehd">
          <div>
            <h2>Reports</h2>
            <p>Pick a report, set filters, preview live, then export to PDF or CSV.</p>
          </div>
        </div>
        <Card pad>
          <EmptyState
            icon="reports"
            title="No reports available"
            description="Reports will appear here once they are configured."
          />
        </Card>
      </div>
    );
  }

  return (
    <div className="pf-fade">
      <div className="pf-pagehd">
        <div>
          <h2>Reports</h2>
          <p>Pick a report, set filters, preview live, then export to PDF or CSV.</p>
        </div>
        <div className="pf-search" style={{ maxWidth: 300, height: 38 }}>
          <Icon name="search" size={16} aria-hidden="true" />
          <input
            placeholder="Search reports…"
            aria-label="Search reports"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
        </div>
      </div>

      <div className="pf-reports-grid">
        {/* Catalog list */}
        <div className="col">
          <CatTabs
            categories={categories}
            active={activeCat}
            onChange={(cat) => {
              setActiveCat(cat);
              setSelectedId(null);
            }}
          />

          {filtered.length === 0 ? (
            <Card pad>
              <EmptyState
                icon="search"
                title="No reports match"
                description="Try a different category or clear the search."
              />
            </Card>
          ) : (
            <div className="pf-report-list" role="list" aria-label="Available reports">
              {filtered.map((r) => (
                <div key={r.id} role="listitem">
                  <ReportCard
                    report={r}
                    active={r.id === effectiveSelectedId}
                    onSelect={() => setSelectedId(r.id)}
                  />
                </div>
              ))}
            </div>
          )}
        </div>

        {/* Builder + preview */}
        {selectedReport ? (
          selectedReport.id === COMPLIANCE_PACK_ID ? (
            <CompliancePackPanel
              key={selectedReport.id}
              report={selectedReport}
              isAdmin={isAdmin}
            />
          ) : (
            <BuilderPanel key={selectedReport.id} report={selectedReport} />
          )
        ) : (
          <Card pad>
            <EmptyState
              icon="reports"
              title="Select a report"
              description="Choose a report from the list to configure and preview it."
            />
          </Card>
        )}
      </div>
    </div>
  );
}
