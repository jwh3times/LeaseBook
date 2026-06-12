import type { ReactNode } from 'react';
import {
  Avatar,
  Badge,
  Button,
  Card,
  CardHeader,
  EmptyState,
  FilterChip,
  Icon,
  ICONS,
  IconButton,
  Input,
  Money,
  ProgressBar,
  Select,
  Sparkline,
  StatCard,
  Table,
  useTheme,
  type Accent,
  type Density,
  type IconName,
  type Theme,
} from '@/design';

const THEMES: Theme[] = ['light', 'dark'];
const ACCENTS: Accent[] = ['teal', 'violet', 'green', 'navy'];
const DENSITIES: Density[] = ['comfortable', 'balanced', 'compact'];

interface DemoRow {
  id: string;
  tenant: string;
  unit: string;
  status: 'Current' | 'Late' | 'Prepaid';
  balance: number;
}

const ROWS: DemoRow[] = [
  { id: 't1', tenant: 'Jasmine Carter', unit: '412 Oakmont Ave · #2B', status: 'Current', balance: 1450 },
  { id: 't3', tenant: 'Aisha Bello', unit: '1029 Charlotte St · #3', status: 'Late', balance: 1620 },
  { id: 't5', tenant: 'The Mercer Family', unit: '88 Riverside Dr', status: 'Prepaid', balance: -75 },
];

const STATUS_TONE = { Current: 'pos', Late: 'neg', Prepaid: 'accent' } as const;

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <Card>
      <CardHeader title={title} />
      <div className="pf-card-pad row gap12" style={{ flexWrap: 'wrap' }}>
        {children}
      </div>
    </Card>
  );
}

export function KitchenSink() {
  const { theme, accent, density, setTheme, setAccent, setDensity } = useTheme();

  return (
    <div className="pf-page col gap16" style={{ minHeight: '100vh' }}>
      <div className="pf-pagehd">
        <div>
          <h2>Design system</h2>
          <p>Every primitive in {theme} · {accent} · {density}. Toggle below to spot-check themes.</p>
        </div>
      </div>

      <Section title="Theme">
        <div className="row gap6">
          {THEMES.map((t) => (
            <FilterChip key={t} active={theme === t} onClick={() => setTheme(t)}>
              {t}
            </FilterChip>
          ))}
        </div>
        <div className="row gap6">
          {ACCENTS.map((a) => (
            <FilterChip key={a} active={accent === a} onClick={() => setAccent(a)}>
              {a}
            </FilterChip>
          ))}
        </div>
        <div className="row gap6">
          {DENSITIES.map((d) => (
            <FilterChip key={d} active={density === d} onClick={() => setDensity(d)}>
              {d}
            </FilterChip>
          ))}
        </div>
      </Section>

      <Section title="Buttons">
        <Button variant="primary" icon="plus">New</Button>
        <Button variant="default">Default</Button>
        <Button variant="soft" icon="download">Export</Button>
        <Button variant="ghost">Ghost</Button>
        <Button variant="default" size="sm">Small</Button>
        <Button variant="primary" disabled>Disabled</Button>
        <IconButton name="bell" label="Notifications" />
        <IconButton name="settings" label="Settings" active />
      </Section>

      <Section title="Badges (status is never color-alone)">
        <Badge tone="pos" dot>Current</Badge>
        <Badge tone="neg" dot>Late</Badge>
        <Badge tone="warn" icon="alert">Review</Badge>
        <Badge tone="accent" dot>Prepaid</Badge>
        <Badge tone="neutral">Draft</Badge>
      </Section>

      <Section title="Money (tabular numerals)">
        <Money value={248930.14} big colorize />
        <Money value={1450} colorize />
        <Money value={-420} colorize />
        <Money value={0} />
        <Money value={-8200} negativeStyle="parens" colorize />
      </Section>

      <Section title="Inputs">
        <Input placeholder="Owner name" style={{ maxWidth: 220 }} />
        <Select defaultValue="oper" style={{ maxWidth: 220 }}>
          <option value="oper">Operating Trust</option>
          <option value="dep">Security Deposit Trust</option>
        </Select>
        <FilterChip icon="filter" active>All</FilterChip>
        <FilterChip>Late only</FilterChip>
      </Section>

      <Section title="Progress & trend">
        <div className="col gap8" style={{ width: 240 }}>
          <ProgressBar pct={89} label="Collected" />
          <ProgressBar pct={62} tone="pos" label="Reconciled" />
        </div>
        <Sparkline data={[42, 58, 71, 88, 96, 100]} />
      </Section>

      <div className="row gap16" style={{ flexWrap: 'wrap' }}>
        <div style={{ minWidth: 240, flex: 1 }}>
          <StatCard label="Trust total" value={<Money value={483620.69} big />} sub="3 accounts" spark={[42, 58, 71, 88, 96, 100]} />
        </div>
        <div style={{ minWidth: 240, flex: 1 }}>
          <StatCard label="Owners payable" value={<Money value={132447} big />} sub="8 owners this cycle" />
        </div>
      </div>

      <Card>
        <CardHeader title="Tenants" sub="Density-aware table" actions={<Button size="sm" variant="soft">Add</Button>} />
        <Table
          rows={ROWS}
          rowKey={(r) => r.id}
          columns={[
            { key: 'tenant', header: 'Tenant', render: (r) => <span className="strong">{r.tenant}</span> },
            { key: 'unit', header: 'Unit', render: (r) => <span className="muted">{r.unit}</span> },
            { key: 'status', header: 'Status', render: (r) => <Badge tone={STATUS_TONE[r.status]} dot>{r.status}</Badge> },
            { key: 'balance', header: 'Balance', num: true, render: (r) => <Money value={r.balance} colorize /> },
          ]}
        />
      </Card>

      <Section title="Empty state">
        <div style={{ width: '100%' }}>
          <EmptyState icon="search" title="No transactions found" description="Try widening the date range or clearing filters." action={<Button variant="soft" size="sm">Clear filters</Button>} />
        </div>
      </Section>

      <Section title="Avatars & icons">
        <Avatar initials="RC" />
        <Avatar initials="HF" size={28} />
        {(Object.keys(ICONS) as IconName[]).map((name) => (
          <span key={name} className="t2" title={name}>
            <Icon name={name} />
          </span>
        ))}
      </Section>
    </div>
  );
}
