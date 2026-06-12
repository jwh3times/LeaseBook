import { render } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { Table, type TableColumn } from './Table';

interface Row {
  id: string;
  name: string;
  amount: number;
}

const rows: Row[] = [
  { id: 'a', name: 'Alpha', amount: 1 },
  { id: 'b', name: 'Beta', amount: 2 },
];

const columns: TableColumn<Row>[] = [
  { key: 'name', header: 'Name', render: (r) => r.name },
  { key: 'amount', header: 'Amount', num: true, render: (r) => r.amount },
];

describe('Table', () => {
  it('renders every row and right-aligns num columns', () => {
    const { container } = render(<Table rows={rows} rowKey={(r) => r.id} columns={columns} />);
    expect(container.querySelectorAll('tbody tr')).toHaveLength(2);

    const headers = container.querySelectorAll('th');
    expect(headers[0]).not.toHaveClass('num');
    expect(headers[1]).toHaveClass('num');
  });

  it('marks rows clickable and invokes onRowClick with the row', () => {
    const onRowClick = vi.fn();
    const { container } = render(
      <Table rows={rows} rowKey={(r) => r.id} columns={columns} onRowClick={onRowClick} />,
    );
    const firstRow = container.querySelector('tbody tr');
    expect(firstRow).toHaveClass('t-row-click');
    (firstRow as HTMLElement).click();
    expect(onRowClick).toHaveBeenCalledWith(rows[0]);
  });
});
