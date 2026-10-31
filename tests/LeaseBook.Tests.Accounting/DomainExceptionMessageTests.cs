using System.Reflection;
using System.Text.RegularExpressions;
using LeaseBook.Modules.Accounting.Contracts;
using Shouldly;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// The enforcement behind ADR-025's error-content rule: a user-facing message may carry money
/// amounts, dates, periods and names — never identifiers, account codes, snake_case database
/// identifiers, or C# type names. Without this test the rule is only a comment, and the next
/// message written re-introduces the leak.
/// </summary>
public sealed class DomainExceptionMessageTests
{
    private static readonly Guid SampleId = Guid.Parse("00000000-0000-0000-0000-00000badbeef");
    private const string SampleCode = "SAMPLE_ACCOUNT_CODE";

    [Fact]
    public void No_domain_exception_message_leaks_an_identifier_or_internal_name()
    {
        var offenders = new List<string>();

        foreach (var exception in ConstructEveryDomainException())
        {
            var message = exception.Message;
            var type = exception.GetType().Name;

            if (message.Contains(SampleId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add($"{type}: leaks a GUID → {message}");
            }

            if (message.Contains(SampleCode, StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add($"{type}: leaks an account code → {message}");
            }

            // snake_case identifiers are database/column names (pm_income, owner_id, source_ref).
            if (Regex.IsMatch(message, @"\b[a-z]{2,}_[a-z_]{2,}\b"))
            {
                offenders.Add($"{type}: leaks a snake_case identifier → {message}");
            }

            // C# type names leaking into user copy.
            if (Regex.IsMatch(message, @"\b\w*(Exception|Service|Handler|Strategy)\b"))
            {
                offenders.Add($"{type}: leaks a type name → {message}");
            }
        }

        offenders.ShouldBeEmpty();
    }

    /// <summary>
    /// The code and message are what reaches the wire: <c>AccountingExceptionHandler</c> maps the code
    /// to a status and serves it as the problem's machine-readable <c>code</c>, with the message as its
    /// <c>detail</c>, and callers such as the payment runner branch on the code. So each is pinned
    /// exactly, for every branch, including a discriminator value no enum member names.
    /// </summary>
    [Fact]
    public void Every_domain_exception_carries_its_exact_code_and_message()
    {
        var mismatches = new List<string>();
        foreach (var (exception, code, message) in Contract())
        {
            var name = exception.GetType().Name;
            if (exception.Code != code)
            {
                mismatches.Add($"{name}: code '{exception.Code}', expected '{code}'");
            }

            if (exception.Message != message)
            {
                mismatches.Add($"{name}: message '{exception.Message}', expected '{message}'");
            }
        }

        mismatches.ShouldBeEmpty();
    }

    [Fact]
    public void The_contract_table_names_every_domain_exception()
    {
        var concrete = typeof(AccountingDomainException).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(AccountingDomainException).IsAssignableFrom(t))
            .Select(t => t.Name);

        Contract().Select(c => c.Exception.GetType().Name).Distinct()
            .ShouldBe(concrete, ignoreOrder: true);
    }

    [Fact]
    public void A_domain_exception_keeps_the_values_its_callers_read()
    {
        var existing = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
        var duplicate = new DuplicateSourceRefException("payment:42", existing);
        duplicate.ExistingEntryId.ShouldBe(existing); // served as the problem's existingEntryId extension
        duplicate.SourceRef.ShouldBe("payment:42");

        var unbalanced = new UnbalancedEntryException("accrual", 100.5m, 90m);
        (unbalanced.Basis, unbalanced.Debits, unbalanced.Credits).ShouldBe(("accrual", 100.5m, 90m));

        var liability = new InsufficientLiabilityException(LiabilityKind.Prepayment, 50m, 20m, SampleId);
        (liability.Kind, liability.Requested, liability.Held, liability.TenantId)
            .ShouldBe((LiabilityKind.Prepayment, 50m, 20m, (Guid?)SampleId));

        var receivable = new InsufficientReceivableException(ReceivableSource.Deposit, 75m, 60m, SampleId);
        (receivable.Kind, receivable.Requested, receivable.Owed, receivable.TenantId)
            .ShouldBe((ReceivableSource.Deposit, 75m, 60m, (Guid?)SampleId));

        var state = new ReconciliationStateException(ReconciliationStateProblem.PeriodFinalized, SampleId, 2026, 5);
        (state.Problem, state.ReconciliationId, state.Year, state.Month)
            .ShouldBe((ReconciliationStateProblem.PeriodFinalized, (Guid?)SampleId, (int?)2026, (int?)5));

        var blocked = new PaymentReturnBlockedException(SampleId, PaymentReturnBlock.ReturnPrecedesReceipt);
        (blocked.EntryId, blocked.Block).ShouldBe((SampleId, PaymentReturnBlock.ReturnPrecedesReceipt));

        new InvalidOpeningPositionException(InvalidOpeningPositionReason.HeldFeesBankRequired).Reason
            .ShouldBe(InvalidOpeningPositionReason.HeldFeesBankRequired);
    }

    /// <summary>Every concrete domain exception, once per message branch, with its code and message.</summary>
    private static IEnumerable<(AccountingDomainException Exception, string Code, string Message)> Contract()
    {
        const string AlreadyFinalized = "That reconciliation period is finalized; unlock it to re-reconcile.";

        yield return (new UnbalancedEntryException("accrual", 100.5m, 90m), "unbalanced_entry",
            "This entry does not balance: debits of 100.50 do not equal credits of 90.00.");

        yield return (new InvalidLineException(InvalidLineReason.NoLines), "invalid_line", "This entry has no lines.");
        yield return (new InvalidLineException(InvalidLineReason.DebitCreditAmbiguous), "invalid_line",
            "Each line must have either a debit or a credit, not both.");
        yield return (new InvalidLineException(InvalidLineReason.NonPositiveAmount), "invalid_line",
            "Each line amount must be greater than zero.");
        yield return (new InvalidLineException((InvalidLineReason)99), "invalid_line", "This entry has an invalid line.");

        yield return (new UnknownAccountException(SampleCode), "unknown_account",
            "The requested account does not exist in this organization.");
        yield return (new PmIncomeOwnerDimException(), "pm_income_owner_dim", "PM income cannot be recorded against an owner.");
        yield return (new PeriodClosedException(2026, 3), "period_closed", "Accounting period 2026-03 is closed.");

        yield return (new InsufficientLiabilityException(LiabilityKind.Deposit, 900m, 500m), "insufficient_liability",
            "Deposit application of 900.00 exceeds the 500.00 held for this tenant.");
        yield return (new InsufficientLiabilityException(LiabilityKind.Prepayment, 50m, 20.5m), "insufficient_liability",
            "Prepayment application of 50.00 exceeds the 20.50 held for this tenant.");
        yield return (new InsufficientLiabilityException(LiabilityKind.FeeSweep, 300m, 200m), "insufficient_liability",
            "Fee sweep of 300.00 exceeds the 200.00 held in the operating trust account.");
        yield return (new InsufficientLiabilityException(LiabilityKind.Refund, 400m, 399.99m), "insufficient_liability",
            "Refund of 400.00 exceeds the 399.99 held for this tenant.");

        yield return (new InsufficientReceivableException(ReceivableSource.Deposit, 75m, 60m), "insufficient_receivable",
            "Deposit application of 75.00 exceeds the 60.00 currently owed by this tenant.");
        yield return (new InsufficientReceivableException(ReceivableSource.Prepayment, 40m, 10m), "insufficient_receivable",
            "Prepayment application of 40.00 exceeds the 10.00 currently owed by this tenant.");
        yield return (new InsufficientReceivableException(ReceivableSource.Credit, 90m, 85m), "insufficient_receivable",
            "A credit of 90.00 exceeds the 85.00 currently owed by this tenant. Only an administrator can allow a larger credit.");

        yield return (new ReserveFloorException(500m, 1200m, 800m, SampleId), "reserve_floor",
            "This disbursement of 500.00 would take the owner's equity from 1200.00 below their 800.00 reserve floor.");

        yield return (new AlreadyReversedException(SampleId, AlreadyReversedReason.AlreadyReversed), "already_reversed",
            "This entry has already been voided.");
        yield return (new AlreadyReversedException(SampleId, AlreadyReversedReason.IsAReversal), "already_reversed",
            "This entry is itself a reversal and cannot be voided.");

        yield return (new AccountPeriodLockedException(SampleId, 2026, 4), "account_period_locked",
            "This bank account is reconciled and locked for 2026-04; post into the open month.");
        yield return (new ReconciliationUnbalancedException(12.3m), "reconciliation_unbalanced",
            "The reconciliation difference is 12.30; clear items until it is 0.00 before finalizing.");

        yield return (new ReconciliationStateException(ReconciliationStateProblem.AlreadyFinalized), "reconciliation_state",
            "That reconciliation is already finalized.");
        yield return (new ReconciliationStateException(ReconciliationStateProblem.NotFinalized), "reconciliation_state",
            "That reconciliation is not finalized — there is nothing to unlock.");
        yield return (new ReconciliationStateException(ReconciliationStateProblem.PeriodFinalized, year: 2026, month: 5),
            "reconciliation_state", "The reconciliation for 2026-05 is finalized; unlock it to re-reconcile.");
        yield return (new ReconciliationStateException(ReconciliationStateProblem.PeriodFinalized, year: 2026),
            "reconciliation_state", AlreadyFinalized);
        yield return (new ReconciliationStateException(ReconciliationStateProblem.PeriodFinalized, month: 5),
            "reconciliation_state", AlreadyFinalized);
        yield return (new ReconciliationStateException(ReconciliationStateProblem.PeriodFinalized),
            "reconciliation_state", AlreadyFinalized);

        yield return (new ReconciliationNotFoundException(SampleId), "reconciliation_not_found", "That reconciliation was not found.");
        yield return (new EntryNotFoundException(SampleId), "entry_not_found", "That entry was not found.");
        yield return (new DuplicateSourceRefException(SampleCode, SampleId), "duplicate_source_ref", "This has already been posted.");
        yield return (new RentObligationAlreadyAssessedException(SampleId, SampleId), "rent_obligation_already_assessed",
            "That rental payment has already been assessed a late fee.");
        yield return (new InvalidAssessmentTargetException(SampleId), "invalid_assessment_target",
            "A late fee must assess a rent charge.");
        yield return (new NoTrustAccountException(), "no_trust_account",
            "No active trust bank account was found. Create one before running disbursements.");
        yield return (new RefundBucketAmbiguousException(SampleId), "refund_bucket_ambiguous",
            "This tenant's held funds sit in more than one account. Choose which one to refund from.");
        yield return (new PrepaymentBankAmbiguousException(SampleId), "prepayment_bank_ambiguous",
            "This tenant's prepaid credit sits in more than one account. Choose which one to apply from.");
        yield return (new RefundCheckClearedException(SampleId), "refund_check_cleared",
            "This check has cleared the bank, so it can no longer be voided.");
        yield return (new PmFeesInsufficientException(30m, 10m, SampleId), "pm_fees_insufficient",
            "The management fees held in this account do not cover the processor fee shortfall.");

        yield return (new PaymentReturnBlockedException(SampleId, PaymentReturnBlock.PrepaymentConsumed),
            "return_prepayment_consumed",
            "Part of this payment became prepaid credit that has since been used, so the return cannot be posted automatically.");
        yield return (new PaymentReturnBlockedException(SampleId, PaymentReturnBlock.ReturnPrecedesReceipt),
            "return_precedes_receipt", "The return is dated before the payment it returns, so it cannot be posted.");
        yield return (new PaymentReturnBlockedException(SampleId, PaymentReturnBlock.OwnerFundsDisbursed),
            "return_owner_funds_disbursed",
            "Funds from this payment have since left the owner's balance, so the return cannot be posted automatically.");

        yield return (new RefundCheckVoidRequiredException(SampleId), "refund_check_void_required",
            "This entry is a refund check. Void it from the check instead.");

        yield return (new InvalidOpeningPositionException(InvalidOpeningPositionReason.HeldFeesBasisMustBeBoth),
            "held_fees_basis_must_be_both", "A held-fees opening must apply to both accounting bases.");
        yield return (new InvalidOpeningPositionException(InvalidOpeningPositionReason.HeldFeesBankRequired),
            "held_fees_bank_required", "A held-fees opening must name the trust bank account holding the fees.");
        yield return (new InvalidOpeningPositionException(InvalidOpeningPositionReason.PmIncomeOwnerDimension),
            "pm_income_owner_dimension", "A held-fees opening cannot be attributed to an owner.");
        yield return (new InvalidOpeningPositionException(InvalidOpeningPositionReason.HeldFeesBankNotTrust),
            "held_fees_bank_not_trust",
            "Held fees can only be imported into a trust bank account, not an operating account.");
        yield return (new InvalidOpeningPositionException((InvalidOpeningPositionReason)99),
            "invalid_opening_position", "This opening position has an invalid shape.");
    }

    /// <summary>
    /// Reflectively builds one instance per concrete domain exception, iterating every value of a
    /// discriminator enum so each message branch is covered.
    /// </summary>
    private static IEnumerable<AccountingDomainException> ConstructEveryDomainException()
    {
        var types = typeof(AccountingDomainException).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(AccountingDomainException).IsAssignableFrom(t))
            .OrderBy(t => t.Name);

        foreach (var type in types)
        {
            var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
            var parameters = ctor.GetParameters();
            var enumIndex = Array.FindIndex(parameters, p => Underlying(p.ParameterType).IsEnum);

            if (enumIndex < 0)
            {
                yield return (AccountingDomainException)ctor.Invoke(
                    [.. parameters.Select(p => SampleValue(p.ParameterType, null))]);
                continue;
            }

            foreach (var enumValue in Enum.GetValues(Underlying(parameters[enumIndex].ParameterType)))
            {
                var args = parameters
                    .Select((p, i) => SampleValue(p.ParameterType, i == enumIndex ? enumValue : null))
                    .ToArray();
                yield return (AccountingDomainException)ctor.Invoke(args);
            }
        }
    }

    private static Type Underlying(Type t) => Nullable.GetUnderlyingType(t) ?? t;

    private static object? SampleValue(Type parameterType, object? enumOverride)
    {
        if (enumOverride is not null)
        {
            return enumOverride;
        }

        var t = Underlying(parameterType);

        if (t.IsEnum) return Enum.GetValues(t).GetValue(0);
        if (t == typeof(Guid)) return SampleId;
        if (t == typeof(decimal)) return 1234.56m;
        if (t == typeof(int)) return 2026;
        if (t == typeof(string)) return SampleCode;

        throw new NotSupportedException(
            $"DomainExceptionMessageTests needs a sample value for {t.Name}. Add one above.");
    }
}
