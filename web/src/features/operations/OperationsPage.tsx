/**
 * Operations page — the tab-based entry point for the three run types + history.
 * Tab selection is the first click; running a report is the second. ≤ 2 clicks to start a run.
 */
import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Button } from '@/design';
import { DisbursementRunScreen } from './DisbursementRunScreen';
import { LateFeeRunScreen } from './LateFeeRunScreen';
import { RentRunScreen } from './RentRunScreen';
import { RunHistoryView } from './RunHistoryView';

type Tab = 'disbursement' | 'rent' | 'latefee' | 'history';

const TABS: { id: Tab; label: string }[] = [
  { id: 'disbursement', label: 'Owner disbursements' },
  { id: 'rent', label: 'Rent charges' },
  { id: 'latefee', label: 'Late fees' },
  { id: 'history', label: 'Run history' },
];

export function OperationsPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  // ?tab=disbursement|rent|latefee|history — supports dashboard CTA deep-link.
  const paramTab = searchParams.get('tab') as Tab | null;
  const [activeTab, setActiveTab] = useState<Tab>(
    TABS.some((t) => t.id === paramTab) ? (paramTab as Tab) : 'disbursement',
  );

  // Sync URL param → tab on first render (dashboard CTA sets ?tab=disbursement).
  useEffect(() => {
    if (paramTab && TABS.some((t) => t.id === paramTab) && paramTab !== activeTab) {
      setActiveTab(paramTab as Tab);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [paramTab]);

  const switchTab = (tab: Tab) => {
    setActiveTab(tab);
    setSearchParams({ tab }, { replace: true });
  };

  return (
    <div className="pf-fade">
      <div className="pf-pagehd">
        <div>
          <h2>Operations</h2>
          <p>Bulk rent charges, late fees, and owner disbursements.</p>
        </div>
      </div>

      {/* Tab bar */}
      <div className="pf-acct-tabs" style={{ marginBottom: 'var(--gap)' }}>
        {TABS.map((tab) => (
          <Button
            key={tab.id}
            variant={activeTab === tab.id ? 'primary' : 'ghost'}
            size="sm"
            onClick={() => switchTab(tab.id)}
            aria-pressed={activeTab === tab.id}
          >
            {tab.label}
          </Button>
        ))}
      </div>

      {/* Tab content */}
      {activeTab === 'disbursement' && <DisbursementRunScreen />}
      {activeTab === 'rent' && <RentRunScreen />}
      {activeTab === 'latefee' && <LateFeeRunScreen />}
      {activeTab === 'history' && <RunHistoryView />}
    </div>
  );
}
