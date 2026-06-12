import type { ReactNode } from 'react';

export interface TableColumn<T> {
  key: string;
  header: ReactNode;
  /** Right-align + tabular numerals (money/quantities). */
  num?: boolean;
  render: (row: T) => ReactNode;
}

export interface TableProps<T> {
  columns: TableColumn<T>[];
  rows: T[];
  rowKey: (row: T) => string;
  onRowClick?: (row: T) => void;
}

// Sticky header, num columns, density-aware (row height/padding come from the active density tokens).
export function Table<T>({ columns, rows, rowKey, onRowClick }: TableProps<T>) {
  return (
    <table className="pf-table">
      <thead>
        <tr>
          {columns.map((column) => (
            <th key={column.key} className={column.num ? 'num' : undefined}>
              {column.header}
            </th>
          ))}
        </tr>
      </thead>
      <tbody>
        {rows.map((row) => (
          <tr
            key={rowKey(row)}
            className={onRowClick ? 't-row-click' : undefined}
            onClick={onRowClick ? () => onRowClick(row) : undefined}
          >
            {columns.map((column) => (
              <td key={column.key} className={column.num ? 'num' : undefined}>
                {column.render(row)}
              </td>
            ))}
          </tr>
        ))}
      </tbody>
    </table>
  );
}
