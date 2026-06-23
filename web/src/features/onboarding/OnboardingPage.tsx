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

export function OnboardingPage() {
  const statusQuery = useOnboardingStatus();

  if (statusQuery.isPending) {
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

  if (statusQuery.isError || !statusQuery.data) {
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

  const status = statusQuery.data;
  const activeStep = firstIncompleteStep(status);

  function handleSelectStep(index: number) {
    // Step navigation handled by panel rendering below
    void index;
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
              description="Upload a CSV for each entity type. Owners, properties, and units must be imported before tenants & leases."
              kinds={[
                { kind: 'owners', label: 'Owners' },
                { kind: 'properties', label: 'Properties' },
                { kind: 'units', label: 'Units' },
                { kind: 'tenants_leases', label: 'Tenants & Leases' },
              ]}
            />
          )}

          {activeStep === 2 && (
            <BalanceImportStep
              title="Import opening balances"
              description="Upload opening balance CSVs. The cutover date must match across all balance types."
              kinds={[
                { kind: 'owner_balances', label: 'Owner balances' },
                { kind: 'deposit_liabilities', label: 'Deposit liabilities' },
                { kind: 'bank_balances', label: 'Bank balances' },
                { kind: 'tenant_receivables', label: 'Tenant receivables' },
              ]}
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
            </Card>
          )}
        </main>
      </div>
    </div>
  );
}
