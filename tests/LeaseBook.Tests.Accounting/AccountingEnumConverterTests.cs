using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Persistence;
using Shouldly;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// The enum texts are a storage contract: the CHECK constraints, the read-model SQL and the seed all
/// spell these exact literals. Each value maps to its text and back, and a value or text outside the
/// set is refused rather than read as some default.
/// </summary>
public sealed class AccountingEnumConverterTests
{
    [Theory]
    [InlineData(AccountClass.TrustBank, "trust_bank")]
    [InlineData(AccountClass.OwnerEquity, "owner_equity")]
    [InlineData(AccountClass.TenantReceivable, "tenant_receivable")]
    [InlineData(AccountClass.DepositLiability, "deposit_liability")]
    [InlineData(AccountClass.PmIncome, "pm_income")]
    [InlineData(AccountClass.PmOperatingBank, "pm_operating_bank")]
    [InlineData(AccountClass.MigrationClearing, "migration_clearing")]
    public void An_account_class_round_trips_through_its_text(AccountClass value, string text)
    {
        AccountClassConverter.ToDb(value).ShouldBe(text);
        AccountClassConverter.FromDb(text).ShouldBe(value);
    }

    [Theory]
    [InlineData(EntryBasis.Cash, "cash")]
    [InlineData(EntryBasis.Accrual, "accrual")]
    [InlineData(EntryBasis.Both, "both")]
    public void An_entry_basis_round_trips_through_its_text(EntryBasis value, string text)
    {
        EntryBasisConverter.ToDb(value).ShouldBe(text);
        EntryBasisConverter.FromDb(text).ShouldBe(value);
    }

    [Theory]
    [InlineData(PeriodStatus.Open, "open")]
    [InlineData(PeriodStatus.Closed, "closed")]
    public void A_period_status_round_trips_through_its_text(PeriodStatus value, string text)
    {
        PeriodStatusConverter.ToDb(value).ShouldBe(text);
        PeriodStatusConverter.FromDb(text).ShouldBe(value);
    }

    [Theory]
    [InlineData(BankLineStatus.Uncleared, "uncleared")]
    [InlineData(BankLineStatus.Cleared, "cleared")]
    [InlineData(BankLineStatus.Reconciled, "reconciled")]
    public void A_bank_line_status_round_trips_through_its_text(BankLineStatus value, string text)
    {
        BankLineStatusConverter.ToDb(value).ShouldBe(text);
        BankLineStatusConverter.FromDb(text).ShouldBe(value);
    }

    [Theory]
    [InlineData(ReconciliationStatus.InProgress, "in_progress")]
    [InlineData(ReconciliationStatus.Finalized, "finalized")]
    [InlineData(ReconciliationStatus.Reopened, "reopened")]
    public void A_reconciliation_status_round_trips_through_its_text(ReconciliationStatus value, string text)
    {
        ReconciliationStatusConverter.ToDb(value).ShouldBe(text);
        ReconciliationStatusConverter.FromDb(text).ShouldBe(value);
    }

    [Fact]
    public void A_value_outside_an_enum_is_refused_with_its_name()
    {
        Refused(() => AccountClassConverter.ToDb((AccountClass)99), "Unknown account class.", (AccountClass)99);
        Refused(() => EntryBasisConverter.ToDb((EntryBasis)99), "Unknown entry basis.", (EntryBasis)99);
        Refused(() => PeriodStatusConverter.ToDb((PeriodStatus)99), "Unknown period status.", (PeriodStatus)99);
        Refused(() => BankLineStatusConverter.ToDb((BankLineStatus)99), "Unknown bank line status.", (BankLineStatus)99);
        Refused(() => ReconciliationStatusConverter.ToDb((ReconciliationStatus)99), "Unknown reconciliation status.",
            (ReconciliationStatus)99);
    }

    [Fact]
    public void A_text_outside_the_set_is_refused_with_its_name()
    {
        // Case matters: the CHECK constraints admit lower case only, so "Cash" is not "cash".
        Refused(() => AccountClassConverter.FromDb("Trust_Bank"), "Unknown account class text.", "Trust_Bank");
        Refused(() => EntryBasisConverter.FromDb("Cash"), "Unknown entry basis text.", "Cash");
        Refused(() => PeriodStatusConverter.FromDb("reopened"), "Unknown period status text.", "reopened");
        Refused(() => BankLineStatusConverter.FromDb(""), "Unknown bank line status text.", "");
        Refused(() => ReconciliationStatusConverter.FromDb("in progress"), "Unknown reconciliation status text.",
            "in progress");
    }

    private static void Refused<T>(Func<T> convert, string message, object actual)
    {
        var refusal = Should.Throw<ArgumentOutOfRangeException>(() => convert());
        refusal.ParamName.ShouldBe("value");
        refusal.ActualValue.ShouldBe(actual);
        refusal.Message.ShouldStartWith(message + " (Parameter 'value')");
    }
}
