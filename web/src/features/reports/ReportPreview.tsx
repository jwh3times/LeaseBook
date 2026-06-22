import { EmptyState, Money } from '@/design';
import { num, type PreviewResponse } from './reports';

// Heuristic: if a column name contains one of these words, render its value as Money.
const MONEY_HINTS = [
  'amount',
  'balance',
  'total',
  'subtotal',
  'operating',
  'deposit',
  'ending',
  'beginning',
  'income',
  'expense',
  'disbursement',
  'rent',
  'fee',
  'credit',
  'debit',
];

function isMoneyColumn(col: string): boolean {
  const lower = col.toLowerCase();
  return MONEY_HINTS.some((h) => lower.includes(h));
}

function renderCell(col: string, value: unknown): React.ReactNode {
  if (value == null) return '—';
  if (isMoneyColumn(col) && (typeof value === 'number' || typeof value === 'string')) {
    const n = num(value as number | string);
    if (!Number.isNaN(n)) return <Money value={n} colorize />;
  }
  return String(value);
}

export interface ReportPreviewProps {
  preview: PreviewResponse;
}

export function ReportPreviewTable({ preview }: ReportPreviewProps) {
  if (preview.rows.length === 0) {
    return (
      <div className="pf-pad">
        <EmptyState
          icon="doc"
          title="No data for this period"
          description="Try adjusting the filters above."
        />
      </div>
    );
  }

  return (
    <table className="pf-table" aria-label="Report preview">
      <thead>
        <tr>
          {preview.columns.map((col) => (
            <th key={col} className={isMoneyColumn(col) ? 'num' : undefined} scope="col">
              {col}
            </th>
          ))}
        </tr>
      </thead>
      <tbody>
        {preview.rows.map((row, i) => (
          // Row index is stable for a given preview response; generic rows have no id field.
          <tr key={`preview-row-${i}`}>
            {preview.columns.map((col) => (
              <td key={col} className={isMoneyColumn(col) ? 'num' : undefined}>
                {renderCell(col, row[col])}
              </td>
            ))}
          </tr>
        ))}
      </tbody>
    </table>
  );
}
