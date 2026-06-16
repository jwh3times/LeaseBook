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
  /** Row key currently selected via keyboard nav — highlighted with <code>aria-selected</code>. */
  selectedKey?: string;
}

// Sticky header, num columns, density-aware (row height/padding come from the active density tokens).
export function Table<T>({ columns, rows, rowKey, onRowClick, selectedKey }: TableProps<T>) {
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
        {rows.map((row) => {
          const key = rowKey(row);
          const selected = selectedKey !== undefined && key === selectedKey;
          return (
            <tr
              key={key}
              className={
                [onRowClick ? 't-row-click' : '', selected ? 't-row-sel' : '']
                  .filter(Boolean)
                  .join(' ') || undefined
              }
              aria-selected={selected || undefined}
              onClick={onRowClick ? () => onRowClick(row) : undefined}
            >
              {columns.map((column) => (
                <td key={column.key} className={column.num ? 'num' : undefined}>
                  {column.render(row)}
                </td>
              ))}
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}
