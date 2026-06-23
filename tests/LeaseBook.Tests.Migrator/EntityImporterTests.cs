using System.Text;
using LeaseBook.Migrator;
using LeaseBook.Migrator.Model;
using LeaseBook.Migrator.Profiles;
using Shouldly;
using Xunit;

namespace LeaseBook.Tests.Migrator;

public sealed class EntityImporterTests
{
    private static Stream Csv(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void OwnerBalances_default_profile_parses_cash_and_accrual_columns()
    {
        var profile = AppFolioProfiles.For(EntityKind.OwnerBalances);
        var result = EntityImporter.ReadOwnerBalances(
            Csv("Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-100,Hargrove,13665.50,13665.50\n"),
            profile);

        result.Errors.ShouldBeEmpty();
        var row = result.Rows.ShouldHaveSingleItem();
        row.ExternalOwnerId.ShouldBe("O-100");
        row.CashBalance.ShouldBe(13665.50m);
        row.AccrualBalance.ShouldBe(13665.50m);
    }

    [Fact]
    public void Non_numeric_balance_is_a_row_error()
    {
        var profile = AppFolioProfiles.For(EntityKind.OwnerBalances);
        var result = EntityImporter.ReadOwnerBalances(
            Csv("Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,x,oops,1\n"), profile);

        result.Rows.ShouldBeEmpty();
        result.Errors.ShouldContain(e => e.RowNumber == 1 && e.Field == "cash_balance");
    }

    [Fact]
    public void BankBalances_default_profile_parses_book_balance()
    {
        var profile = AppFolioProfiles.For(EntityKind.BankBalances);
        var result = EntityImporter.ReadBankBalances(
            Csv("Account ID,Account Name,Book Balance\nB-1,Operating Trust,42500.00\n"), profile);

        result.Errors.ShouldBeEmpty();
        var row = result.Rows.ShouldHaveSingleItem();
        row.ExternalBankId.ShouldBe("B-1");
        row.Name.ShouldBe("Operating Trust");
        row.BookBalance.ShouldBe(42500.00m);
    }

    [Fact]
    public void Non_numeric_book_balance_is_a_row_error()
    {
        var profile = AppFolioProfiles.For(EntityKind.BankBalances);
        var result = EntityImporter.ReadBankBalances(
            Csv("Account ID,Account Name,Book Balance\nB-1,Operating Trust,oops\n"), profile);

        result.Rows.ShouldBeEmpty();
        result.Errors.ShouldContain(e => e.RowNumber == 1 && e.Field == "book_balance");
    }
}
