import { Card, CardHeader, Icon } from '@/design';

// Static in-app reference for the parallel-run overlap period.
// No data fetching — this is operator guidance, not live data.
// Source of truth: docs/migration/parallel-run.md

const DURING_OVERLAP: string[] = [
  'Rent received — record in AppFolio AND post in LeaseBook (tenant ledger > Apply Payment).',
  'Owner disbursements — record in AppFolio AND post in LeaseBook (owner ledger > Disburse).',
  'Management fees — charge in both systems on the same date.',
  'Security deposit movements — apply or refund in both systems simultaneously.',
  'Maintenance / expense payments — record in both if they touch the trust account.',
  'Bank deposits and withdrawals — clear items in the LeaseBook bank register as they clear your actual bank.',
];

interface TieRow {
  figure: string;
  appfolio: string;
  leasebook: string;
}

const TIE_ROWS: TieRow[] = [
  {
    figure: 'Owner ending balance (per owner)',
    appfolio: 'Owner Statement — Ending Balance column',
    leasebook: 'Owner detail page — ending balance',
  },
  {
    figure: 'Total deposit liabilities',
    appfolio: 'Security Deposit Liability report',
    leasebook: 'Dashboard — Deposit Liabilities KPI',
  },
  {
    figure: 'Bank book balance (per trust account)',
    appfolio: 'Bank Account Detail — Book Balance',
    leasebook: 'Banking register — book balance',
  },
  {
    figure: 'Tenant balance due (per tenant)',
    appfolio: 'Tenant Balance / Aged Receivables',
    leasebook: 'Tenant ledger — balance',
  },
];

const SIGNOFF_CRITERIA: string[] = [
  'All figures in the month-end comparison table tie to the cent.',
  'The LeaseBook migration verification screen shows Tied with $0.00 variance.',
  'You have reconciled at least one bank account in LeaseBook for the overlap month.',
  'You are comfortable with the LeaseBook workflow for daily operations.',
];

export function ParallelRunReference() {
  return (
    <div className="pf-fade">
      <div className="pf-pagehd">
        <div>
          <h2>Parallel-Run Reference</h2>
          <p className="t3 fs14 mt4">
            Checklist for the overlap month — enter transactions in both systems and tie figures at
            month-end before going fully live.
          </p>
        </div>
      </div>

      <div className="col gap16">
        {/* Section 1 — During overlap */}
        <Card pad>
          <CardHeader
            title="During the overlap month — enter in BOTH systems"
            sub="For every transaction that occurs after the cutover date, record it in AppFolio and LeaseBook."
          />
          <ul className="ob-parallel-list" aria-label="Transactions to enter in both systems">
            {DURING_OVERLAP.map((item) => (
              <li key={item} className="ob-parallel-list-item">
                <span className="ob-parallel-check" aria-hidden>
                  <Icon name="arrowUpRight" size={14} />
                </span>
                <span className="fs14">{item}</span>
              </li>
            ))}
          </ul>
          <p className="fs13 t3 mt12">
            Tip: batch your daily entry at the same time you close out AppFolio. This keeps the two
            systems in sync and makes month-end comparison fast.
          </p>
        </Card>

        {/* Section 2 — Month-end comparison */}
        <Card pad>
          <CardHeader
            title="Month-end comparison — tie these figures to the cent"
            sub="Pull these figures from AppFolio as of the last day of the overlap month and compare to LeaseBook."
          />
          <div className="pf-table-wrap mt8">
            <table className="pf-table" aria-label="Month-end tie-out figures">
              <thead>
                <tr>
                  <th>Figure</th>
                  <th>AppFolio source</th>
                  <th>LeaseBook source</th>
                </tr>
              </thead>
              <tbody>
                {TIE_ROWS.map((row) => (
                  <tr key={row.figure}>
                    <td className="fw5">{row.figure}</td>
                    <td className="fs13 t3">{row.appfolio}</td>
                    <td className="fs13 t3">{row.leasebook}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="ob-inline-note mt12">
            <Icon name="alert" size={14} />
            <span className="fs13">
              If any figure is off: find the first date the two systems diverge, identify the
              missing or double-counted transaction in LeaseBook, correct it with a void + re-entry
              or reversal, then re-compare.
            </span>
          </div>
        </Card>

        {/* Section 3 — Sign-off criteria */}
        <Card pad>
          <CardHeader
            title="Sign-off criteria"
            sub="You are ready to go fully live on LeaseBook when all of these are true."
          />
          <ul className="ob-parallel-list" aria-label="Sign-off criteria">
            {SIGNOFF_CRITERIA.map((item) => (
              <li key={item} className="ob-parallel-list-item">
                <span className="ob-parallel-check" aria-hidden>
                  <Icon name="check" size={14} />
                </span>
                <span className="fs14">{item}</span>
              </li>
            ))}
          </ul>
          <p className="fs13 t3 mt12">
            Once all criteria are met, stop entering data in AppFolio. LeaseBook is your system of
            record.
          </p>
        </Card>

        {/* Section 4 — Links */}
        <Card pad>
          <CardHeader title="Reference" />
          <ul className="col gap8 fs14 mt4">
            <li>
              <a href="/onboarding" className="pf-link">
                Migration Setup wizard
              </a>{' '}
              — return to onboarding steps
            </li>
            <li>
              <a href="/banking" className="pf-link">
                Banking
              </a>{' '}
              — reconcile the opening period
            </li>
          </ul>
        </Card>
      </div>
    </div>
  );
}
