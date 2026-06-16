import { Badge, type BadgeTone } from '@/design';

// Status is never color-alone (CLAUDE.md UX contract): every badge pairs a dot + the capitalized label.
const TENANT_TONE: Record<string, BadgeTone> = {
  current: 'pos',
  late: 'warn',
  prepaid: 'accent',
  evicting: 'neg',
  past: 'neutral',
};

const UNIT_TONE: Record<string, BadgeTone> = {
  occupied: 'pos',
  vacant: 'neutral',
  unavailable: 'warn',
};

const LEASE_TONE: Record<string, BadgeTone> = {
  active: 'pos',
  pending: 'accent',
  ended: 'neutral',
};

function titleCase(value: string): string {
  return value.charAt(0).toUpperCase() + value.slice(1);
}

export function TenantStatusBadge({ status }: { status: string }) {
  return (
    <Badge tone={TENANT_TONE[status] ?? 'neutral'} dot>
      {titleCase(status)}
    </Badge>
  );
}

export function UnitStatusBadge({ status }: { status: string }) {
  return (
    <Badge tone={UNIT_TONE[status] ?? 'neutral'} dot>
      {titleCase(status)}
    </Badge>
  );
}

export function LeaseStatusBadge({ status }: { status: string }) {
  return (
    <Badge tone={LEASE_TONE[status] ?? 'neutral'} dot>
      {titleCase(status)}
    </Badge>
  );
}
