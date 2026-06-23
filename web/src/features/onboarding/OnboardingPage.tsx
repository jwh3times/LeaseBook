import { useEffect, useState } from 'react';
import { Card, CardHeader, EmptyState } from '@/design';
import { BalanceImportStep, EntityImportStep } from './ImportStep';
import { OnboardingChecklist } from './OnboardingChecklist';
import { useOnboardingStatus } from './onboarding';
import { VerificationStep } from './VerificationStep';

/**
 * Derives the first incomplete wizard step (0-indexed).
 * Step order: 0=banks, 1=entities, 2=balances, 3=verify, 4=reconcile
 */
function firstIncompleteStep(status: {
  banksConfigured: boolean;
  entitiesImported: boolean;
  balancesImported: boolean;
  signedOff: boolean;
}): number {
  if (!status.banksConfigured) return 0;
  if (!status.entitiesImported) return 1;
  if (!status.balancesImported) return 2;
  if (!status.signedOff) return 3;
  return 4;
}

const LAST_STEP = 4;

export function OnboardingPage() {
  const statusQuery = useOnboardingStatus();
  // The active wizard step. Pinned once status first loads — step advancement is then
  // EXPLICIT (the operator clicks "Continue" or a reached checklist item), never reactive.
  // A reactive jump (re-deriving from status on every query invalidation) would yank the
  // operator off the entity step the moment one import succeeds, before they finish importing
  // all four kinds. null = not yet initialised (status still loading).
  const [activeStep, setActiveStep] = useState<number | null>(null);

  // Seed the active step once, from the first-incomplete step, when status first becomes known.
  // This is a one-time landing/resume; later status invalidations (after an import) do NOT move it.
  const status = statusQuery.data;
  useEffect(() => {
    if (status && activeStep === null) {
      setActiveStep(firstIncompleteStep(status));
    }
  }, [status, activeStep]);

  if (statusQuery.isPending || activeStep === null) {
    return (
      <div className="pf-fade">
        <div className="pf-pagehd">
          <h2>Migration Setup</h2>
        </div>
        <Card pad>
          <div className="col gap12">
            {Array.from({ length: 5 }).map((_, i) => (
              <div key={i} className="pf-skeleton" style={{ height: 32 }} />
            ))}
          </div>
        </Card>
      </div>
    );
  }

  if (statusQuery.isError || !status) {
    return (
      <div className="pf-fade">
        <Card pad>
          <EmptyState
            icon="alert"
            title="Couldn't load onboarding status"
            description="Try again in a moment."
          />
        </Card>
      </div>
    );
  }

  // The furthest step the operator has reached — derived from status so the checklist still
  // reflects real completion and backward navigation is gated to already-reached steps.
  const firstIncomplete = firstIncompleteStep(status);
  // The operator may navigate back to any reached step (≤ the furthest reached step) or forward
  // only via the explicit "Continue" affordance. The ceiling is the larger of the derived
  // first-incomplete step and the pinned active step (so a forward "Continue" stays reachable).
  const reachedCeiling = Math.max(firstIncomplete, activeStep);

  function handleSelectStep(index: number) {
    // Allow navigation only to steps already reached (≤ the furthest reached step).
    if (index <= reachedCeiling) setActiveStep(index);
  }

  function goToNextStep() {
    setActiveStep((prev) => Math.min((prev ?? 0) + 1, LAST_STEP));
  }

  return (
    <div className="pf-fade">
      <div className="pf-pagehd">
        <div>
          <h2>Migration Setup</h2>
          <p className="t3 fs14 mt4">
            Complete these steps to migrate from AppFolio and go live on LeaseBook.
          </p>
        </div>
      </div>

      <div className="ob-wizard-layout">
        <aside className="ob-wizard-sidebar">
          <Card>
            <CardHeader title="Steps" />
            <OnboardingChecklist
              status={status}
              activeStep={activeStep}
              onSelectStep={handleSelectStep}
            />
          </Card>
        </aside>

        <main className="ob-wizard-main">
          {activeStep === 0 && (
            <Card pad>
              <CardHeader
                title="Set up trust bank accounts"
                sub="Configure your trust, deposit, and operating bank accounts before importing data."
              />
              <p className="fs14 mt8">
                Go to{' '}
                <a href="/settings" className="pf-link">
                  Settings → Bank accounts
                </a>{' '}
                to add your trust bank accounts, then return here.
              </p>
            </Card>
          )}

          {activeStep === 1 && (
            <EntityImportStep
              title="Import entities"
              description="Upload a CSV for each entity type. Owners, properties, and units must be imported before tenants & leases. Import all four, then click Continue."
              kinds={[
                { kind: 'owners', label: 'Owners' },
                { kind: 'properties', label: 'Properties' },
                { kind: 'units', label: 'Units' },
                { kind: 'tenants_leases', label: 'Tenants & Leases' },
              ]}
              onContinue={goToNextStep}
            />
          )}

          {activeStep === 2 && (
            <BalanceImportStep
              title="Import opening balances"
              description="Upload opening balance CSVs. The cutover date must match across all balance types. Import each, then click Continue."
              kinds={[
                { kind: 'owner_balances', label: 'Owner balances' },
                { kind: 'deposit_liabilities', label: 'Deposit liabilities' },
                { kind: 'bank_balances', label: 'Bank balances' },
                { kind: 'tenant_receivables', label: 'Tenant receivables' },
              ]}
              onContinue={goToNextStep}
            />
          )}

          {activeStep === 3 && <VerificationStep />}

          {activeStep === 4 && (
            <Card pad>
              <CardHeader
                title="Reconcile first month"
                sub="Migration is complete. Open the bank register to clear your first month of transactions."
              />
              <p className="fs14 mt8">
                Go to{' '}
                <a href="/banking" className="pf-link">
                  Banking
                </a>{' '}
                to reconcile your opening period.
              </p>
              <p className="fs14 mt8">
                Running both systems in parallel for an overlap month?{' '}
                <a href="/onboarding/parallel-run" className="pf-link">
                  Parallel-run checklist
                </a>{' '}
                — what to enter in both systems and how to tie figures at month-end.
              </p>
            </Card>
          )}
        </main>
      </div>
    </div>
  );
}
