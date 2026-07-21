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

    // -------------------------------------------------------------------------
    // Balance binder tests (pre-existing)
    // -------------------------------------------------------------------------

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

    // -------------------------------------------------------------------------
    // Entity binder tests (WP-3 Task 3.1)
    // -------------------------------------------------------------------------

    [Fact]
    public void ReadOwners_happy_path_parses_all_fields()
    {
        var profile = AppFolioProfiles.For(EntityKind.Owners);
        var result = EntityImporter.ReadOwners(
            Csv("Owner ID,Owner Name,Reserve\nO-1,Hargrove Family Trust,250.00\n"), profile);

        result.Errors.ShouldBeEmpty();
        var row = result.Rows.ShouldHaveSingleItem();
        row.ExternalId.ShouldBe("O-1");
        row.Name.ShouldBe("Hargrove Family Trust");
        row.Reserve.ShouldBe(250.00m);
    }

    [Fact]
    public void ReadOwners_optional_reserve_defaults_to_zero_when_absent()
    {
        var profile = AppFolioProfiles.For(EntityKind.Owners);
        var result = EntityImporter.ReadOwners(
            Csv("Owner ID,Owner Name\nO-2,Linden Properties LLC\n"), profile);

        result.Errors.ShouldBeEmpty();
        result.Rows.ShouldHaveSingleItem().Reserve.ShouldBe(0m);
    }

    [Fact]
    public void ReadOwners_missing_name_is_a_row_error()
    {
        var profile = AppFolioProfiles.For(EntityKind.Owners);
        var result = EntityImporter.ReadOwners(
            Csv("Owner ID,Owner Name,Reserve\nO-3,,100.00\n"), profile);

        result.Rows.ShouldBeEmpty();
        result.Errors.ShouldContain(e => e.RowNumber == 1 && e.Field == "name");
    }

    [Fact]
    public void ReadOwners_one_bad_row_does_not_sink_the_batch()
    {
        var profile = AppFolioProfiles.For(EntityKind.Owners);
        var result = EntityImporter.ReadOwners(
            Csv("Owner ID,Owner Name,Reserve\nO-1,Good Owner,100.00\nO-2,,200.00\n"), profile);

        // Row 1 is valid; row 2 has an empty name and is rejected.
        result.Rows.ShouldHaveSingleItem().Name.ShouldBe("Good Owner");
        result.Errors.ShouldContain(e => e.RowNumber == 2 && e.Field == "name");
    }

    [Fact]
    public void ReadOwners_two_valid_rows_parsed()
    {
        var profile = AppFolioProfiles.For(EntityKind.Owners);
        var result = EntityImporter.ReadOwners(
            Csv("Owner ID,Owner Name,Reserve\nO-1,Alpha LLC,0\nO-2,Beta LLC,500\n"), profile);

        result.Errors.ShouldBeEmpty();
        result.Rows.Count.ShouldBe(2);
        result.Rows[0].ExternalId.ShouldBe("O-1");
        result.Rows[1].ExternalId.ShouldBe("O-2");
    }

    [Fact]
    public void ReadProperties_happy_path()
    {
        var profile = AppFolioProfiles.For(EntityKind.Properties);
        var result = EntityImporter.ReadProperties(
            Csv("Property ID,Owner ID,Address\nP-1,O-1,123 Main St\n"), profile);

        result.Errors.ShouldBeEmpty();
        var row = result.Rows.ShouldHaveSingleItem();
        row.ExternalId.ShouldBe("P-1");
        row.ExternalOwnerId.ShouldBe("O-1");
        row.Address.ShouldBe("123 Main St");
    }

    [Fact]
    public void ReadProperties_missing_owner_id_is_a_row_error()
    {
        var profile = AppFolioProfiles.For(EntityKind.Properties);
        var result = EntityImporter.ReadProperties(
            Csv("Property ID,Owner ID,Address\nP-1,,123 Main St\n"), profile);

        result.Rows.ShouldBeEmpty();
        result.Errors.ShouldContain(e => e.RowNumber == 1 && e.Field == "external_owner_id");
    }

    [Fact]
    public void ReadUnits_happy_path_with_optional_defaults()
    {
        var profile = AppFolioProfiles.For(EntityKind.Units);
        var result = EntityImporter.ReadUnits(
            Csv("Unit ID,Property ID,Unit\nU-1,P-1,Unit A\n"), profile);

        result.Errors.ShouldBeEmpty();
        var row = result.Rows.ShouldHaveSingleItem();
        row.ExternalId.ShouldBe("U-1");
        row.ExternalPropertyId.ShouldBe("P-1");
        row.Label.ShouldBe("Unit A");
        row.Rent.ShouldBe(0m);       // optional, absent → 0
        row.Status.ShouldBe("vacant"); // optional, absent → "vacant"
    }

    [Fact]
    public void ReadTenantsLeases_happy_path_parses_dates_and_amounts()
    {
        var profile = AppFolioProfiles.For(EntityKind.TenantsLeases);
        var result = EntityImporter.ReadTenantsLeases(
            Csv("Tenant ID,Unit ID,Tenant Name,Lease Start,Lease End,Rent,Deposit,Status\n" +
                "T-1,U-1,Jane Smith,2025-01-01,2026-01-01,1200.00,2400.00,active\n"), profile);

        result.Errors.ShouldBeEmpty();
        var row = result.Rows.ShouldHaveSingleItem();
        row.ExternalId.ShouldBe("T-1");
        row.ExternalUnitId.ShouldBe("U-1");
        row.DisplayName.ShouldBe("Jane Smith");
        row.StartDate.ShouldBe(new DateOnly(2025, 1, 1));
        row.EndDate.ShouldBe(new DateOnly(2026, 1, 1));
        row.Rent.ShouldBe(1200.00m);
        row.DepositRequired.ShouldBe(2400.00m);
        row.Status.ShouldBe("active");
    }

    [Fact]
    public void ReadTenantsLeases_optional_dates_absent_produces_null()
    {
        var profile = AppFolioProfiles.For(EntityKind.TenantsLeases);
        var result = EntityImporter.ReadTenantsLeases(
            Csv("Tenant ID,Unit ID,Tenant Name\nT-2,U-2,Bob Jones\n"), profile);

        result.Errors.ShouldBeEmpty();
        var row = result.Rows.ShouldHaveSingleItem();
        row.StartDate.ShouldBeNull();
        row.EndDate.ShouldBeNull();
        row.Status.ShouldBe("active"); // default
    }

    [Fact]
    public void ReadHeldPmFees_binds_bank_name_and_amount()
    {
        var csv = "Account ID,Account Name,Held Fees\nB-TRUST,Trust Operating,125.50\n";
        var result = EntityImporter.ReadHeldPmFees(Csv(csv), AppFolioProfiles.For(EntityKind.HeldPmFees));
        result.Errors.ShouldBeEmpty();
        result.Rows.Count.ShouldBe(1);
        result.Rows[0].ShouldBe(new HeldPmFeeRow("B-TRUST", "Trust Operating", 125.50m));
    }

    [Fact]
    public void ReadHeldPmFees_rejects_non_numeric_amount_and_keeps_going()
    {
        var csv = "Account ID,Account Name,Held Fees\nB-1,Trust A,abc\nB-2,Trust B,50.00\n";
        var result = EntityImporter.ReadHeldPmFees(Csv(csv), AppFolioProfiles.For(EntityKind.HeldPmFees));
        result.Errors.Count.ShouldBe(1);
        result.Errors[0].Field.ShouldBe("held_amount");
        result.Rows.Count.ShouldBe(1);
    }

    [Fact]
    public void Every_entity_kind_resolves_a_profile()   // guards AppFolioProfiles' throwing default (R9)
    {
        foreach (var kind in Enum.GetValues<EntityKind>())
            Should.NotThrow(() => AppFolioProfiles.For(kind), $"no profile for {kind}");
    }
}
