import { useNavigate } from 'react-router-dom';
import { Icon, type IconName } from '@/design';
import type { OnboardingStatusResponse } from './onboarding';

interface Step {
  id: string;
  label: string;
  description: string;
  done: boolean;
  doneIcon: IconName;
  pendingIcon: IconName;
  ariaStatus: string;
}

function StepIcon({
  done,
  doneIcon,
  pendingIcon,
  active,
}: {
  done: boolean;
  doneIcon: IconName;
  pendingIcon: IconName;
  active: boolean;
}) {
  if (done) {
    return (
      <span className="ob-step-icon ob-step-icon--done" aria-hidden>
        <Icon name={doneIcon} size={18} />
      </span>
    );
  }
  if (active) {
    return (
      <span className="ob-step-icon ob-step-icon--active" aria-hidden>
        <Icon name={pendingIcon} size={18} />
      </span>
    );
  }
  return (
    <span className="ob-step-icon ob-step-icon--waiting" aria-hidden>
      <Icon name={pendingIcon} size={18} />
    </span>
  );
}

interface OnboardingChecklistProps {
  status: OnboardingStatusResponse;
  activeStep: number;
  onSelectStep: (index: number) => void;
}

export function OnboardingChecklist({
  status,
  activeStep,
  onSelectStep,
}: OnboardingChecklistProps) {
  const navigate = useNavigate();

  const steps: Step[] = [
    {
      id: 'banks',
      label: 'Set up trust bank accounts',
      description: 'Configure your trust, deposit, and operating bank accounts.',
      done: status.banksConfigured,
      doneIcon: 'check',
      pendingIcon: 'wallet',
      ariaStatus: status.banksConfigured ? 'complete' : 'pending',
    },
    {
      id: 'entities',
      label: 'Import entities',
      description: 'Import owners, properties, units, and tenant leases.',
      done: status.entitiesImported,
      doneIcon: 'check',
      pendingIcon: 'arrowUpRight',
      ariaStatus: status.entitiesImported ? 'complete' : 'pending',
    },
    {
      id: 'balances',
      label: 'Import opening balances',
      description:
        'Import owner balances, deposit liabilities, bank balances, and tenant receivables.',
      done: status.balancesImported,
      doneIcon: 'check',
      pendingIcon: 'arrowUpRight',
      ariaStatus: status.balancesImported ? 'complete' : 'pending',
    },
    {
      id: 'verify',
      label: 'Verify & sign off',
      description: 'Enter your AppFolio closing figures, confirm the import ties, and sign off.',
      done: status.signedOff,
      doneIcon: 'check',
      pendingIcon: 'alert',
      ariaStatus: status.signedOff ? 'complete' : 'pending',
    },
    {
      id: 'reconcile',
      label: 'Reconcile first month',
      description: 'Open the bank register and clear your first month of transactions.',
      done: false,
      doneIcon: 'check',
      pendingIcon: 'arrowUpRight',
      ariaStatus: 'pending',
    },
  ];

  function handleStepClick(index: number, stepId: string) {
    if (stepId === 'banks') {
      navigate('/settings');
      return;
    }
    if (stepId === 'reconcile') {
      navigate('/banking');
      return;
    }
    onSelectStep(index);
  }

  return (
    <ol className="ob-checklist" aria-label="Onboarding steps">
      {steps.map((step, i) => {
        const isActive = i === activeStep;
        const stepNumber = i + 1;

        return (
          <li
            key={step.id}
            className={[
              'ob-checklist-item',
              step.done ? 'ob-checklist-item--done' : '',
              isActive ? 'ob-checklist-item--active' : '',
            ]
              .filter(Boolean)
              .join(' ')}
            aria-current={isActive ? 'step' : undefined}
            aria-label={`Step ${stepNumber}: ${step.label} — ${step.ariaStatus}`}
          >
            {/* Interactive wrapper — button for clickable steps, plain div for nav links */}
            <button
              type="button"
              className="ob-step-btn"
              onClick={() => handleStepClick(i, step.id)}
              aria-label={`Go to step ${stepNumber}: ${step.label}`}
            >
              <StepIcon
                done={step.done}
                doneIcon={step.doneIcon}
                pendingIcon={step.pendingIcon}
                active={isActive}
              />
              <div className="ob-checklist-text">
                <span className="ob-step-label">{step.label}</span>
                {isActive && <span className="ob-step-desc">{step.description}</span>}
              </div>
              {(step.id === 'banks' || step.id === 'reconcile') && (
                <span className="ob-step-link-hint" aria-hidden>
                  <Icon name="arrowUpRight" size={14} />
                </span>
              )}
            </button>
          </li>
        );
      })}
    </ol>
  );
}
