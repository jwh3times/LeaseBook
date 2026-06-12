import type { ReactNode } from 'react';
import { Card } from './Card';
import { Sparkline } from './Sparkline';

export interface StatCardProps {
  label: string;
  value: ReactNode;
  sub?: ReactNode;
  /** Optional sparkline trend. */
  spark?: number[];
}

export function StatCard({ label, value, sub, spark }: StatCardProps) {
  return (
    <Card pad>
      <div className="col gap8">
        <span className="pf-stat-label">{label}</span>
        <div className="row between gap12">
          <div className="col gap4">
            <div>{value}</div>
            {sub && <span className="fs12 t3">{sub}</span>}
          </div>
          {spark && <Sparkline data={spark} />}
        </div>
      </div>
    </Card>
  );
}
