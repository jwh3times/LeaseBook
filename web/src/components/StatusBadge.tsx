import { Badge, type BadgeTone, type IconName } from '@/design';

// Status is never color-alone (CLAUDE.md UX contract): every badge pairs a dot + the capitalized label.
const TENANT_LIFECYCLE_TONE: Record<string, BadgeTone> = {
  current: 'pos',
  evicting: 'neg',
  past: 'neutral',
};

const UNIT_OCCUPANCY_TONE: Record<string, BadgeTone> = {
  occupied: 'pos',
  vacant: 'neutral',
};

const UNIT_AVAILABILITY_TONE: Record<string, BadgeTone> = {
  available: 'pos',
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

export function TenantLifecycleBadge({ status }: { status: string }) {
  return (
    <Badge tone={TENANT_LIFECYCLE_TONE[status] ?? 'neutral'} dot>
      {titleCase(status)}
    </Badge>
  );
}

export function TenantFinancialStandingBadges({
  delinquentBalance,
  unappliedCredit,
}: {
  delinquentBalance: number;
  unappliedCredit: number;
}) {
  if (delinquentBalance <= 0 && unappliedCredit <= 0) {
    return (
      <Badge tone="neutral" dot>
        No alerts
      </Badge>
    );
  }

  return (
    <span className="row gap4" style={{ flexWrap: 'wrap' }}>
      {delinquentBalance > 0 && (
        <Badge tone="warn" dot>
          Delinquent
        </Badge>
      )}
      {unappliedCredit > 0 && (
        <Badge tone="accent" dot>
          Credit on file
        </Badge>
      )}
    </span>
  );
}

export function UnitOccupancyBadge({ occupancy }: { occupancy: string }) {
  return (
    <Badge tone={UNIT_OCCUPANCY_TONE[occupancy] ?? 'neutral'} dot>
      {titleCase(occupancy)}
    </Badge>
  );
}

export function UnitAvailabilityBadge({ availability }: { availability: string }) {
  return (
    <Badge tone={UNIT_AVAILABILITY_TONE[availability] ?? 'neutral'} dot>
      {titleCase(availability)}
    </Badge>
  );
}

// A refund check's lifecycle (#473): derived server-side from the bank line's clearance or a linked
// reversal. Each status carries its own glyph as well as its word, so the four stay distinguishable
// with no colour at all.
const REFUND_CHECK_STATUS: Record<string, { tone: BadgeTone; icon: IconName; label: string }> = {
  outstanding: { tone: 'warn', icon: 'clock', label: 'Outstanding' },
  cleared: { tone: 'accent', icon: 'check', label: 'Cleared' },
  reconciled: { tone: 'pos', icon: 'lock', label: 'Reconciled' },
  voided: { tone: 'neutral', icon: 'x', label: 'Voided' },
};

export function RefundCheckStatusBadge({ status }: { status: string }) {
  const meta = REFUND_CHECK_STATUS[status] ?? {
    tone: 'neutral' as const,
    icon: 'info' as const,
    label: titleCase(status),
  };
  return (
    <Badge tone={meta.tone} icon={meta.icon}>
      {meta.label}
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
