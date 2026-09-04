using System.Text;
using LeaseBook.Migrator;
using LeaseBook.Migrator.Model;
using Shouldly;
using Xunit;

namespace LeaseBook.Tests.Migrator;

public sealed class AppFolioImportCatalogTests
{
    private static Stream Csv(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    // -------------------------------------------------------------------------
    // Balance binder tests (pre-existing)
    // -------------------------------------------------------------------------

    [Fact]
    public void OwnerBalances_default_profile_parses_cash_and_accrual_columns()
    {
        var result = AppFolioImportCatalog.OwnerBalances.Read(
            Csv("Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-100,Hargrove,13665.50,13665.50\n"));

        result.Errors.ShouldBeEmpty();
        var row = result.Rows.ShouldHaveSingleItem();
        row.ExternalOwnerId.ShouldBe("O-100");
        row.CashBalance.ShouldBe(13665.50m);
        row.AccrualBalance.ShouldBe(13665.50m);
    }

    [Fact]
    public void Non_numeric_balance_is_a_row_error()
    {
        var result = AppFolioImportCatalog.OwnerBalances.Read(
            Csv("Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,x,oops,1\n"));

        result.Rows.ShouldBeEmpty();
        result.Errors.ShouldContain(e => e.RowNumber == 1 && e.Field == "cash_balance");
    }

    [Fact]
    public void BankBalances_default_profile_parses_book_balance()
    {
        var result = AppFolioImportCatalog.BankBalances.Read(
            Csv("Account ID,Account Name,Book Balance\nB-1,Operating Trust,42500.00\n"));

        result.Errors.ShouldBeEmpty();
        var row = result.Rows.ShouldHaveSingleItem();
        row.ExternalBankId.ShouldBe("B-1");
        row.Name.ShouldBe("Operating Trust");
        row.BookBalance.ShouldBe(42500.00m);
    }

    [Fact]
    public void DepositLiabilities_default_profile_parses_held_amount()
    {
        var result = AppFolioImportCatalog.DepositLiabilities.Read(
            Csv("Tenant ID,Owner ID,Deposit Held\nT-1,O-1,750.00\n"));

        result.Errors.ShouldBeEmpty();
        result.Rows.ShouldHaveSingleItem().ShouldBe(new DepositLiabilityRow("T-1", "O-1", 750.00m));
    }

    [Fact]
    public void TenantReceivables_default_profile_parses_balance()
    {
        var result = AppFolioImportCatalog.TenantReceivables.Read(
            Csv("Tenant ID,Owner ID,Balance Due\nT-1,O-1,325.00\n"));

        result.Errors.ShouldBeEmpty();
        result.Rows.ShouldHaveSingleItem().ShouldBe(new TenantReceivableRow("T-1", "O-1", 325.00m));
    }

    [Fact]
    public void Non_numeric_book_balance_is_a_row_error()
    {
        var result = AppFolioImportCatalog.BankBalances.Read(
            Csv("Account ID,Account Name,Book Balance\nB-1,Operating Trust,oops\n"));

        result.Rows.ShouldBeEmpty();
        result.Errors.ShouldContain(e => e.RowNumber == 1 && e.Field == "book_balance");
    }

    // -------------------------------------------------------------------------
    // Entity binder tests (WP-3 Task 3.1)
    // -------------------------------------------------------------------------

    [Fact]
    public void ReadOwners_happy_path_parses_all_fields()
    {
        var result = AppFolioImportCatalog.Owners.Read(
            Csv("Owner ID,Owner Name,Reserve\nO-1,Hargrove Family Trust,250.00\n"));

        result.Errors.ShouldBeEmpty();
        var row = result.Rows.ShouldHaveSingleItem();
        row.ExternalId.ShouldBe("O-1");
        row.Name.ShouldBe("Hargrove Family Trust");
        row.Reserve.ShouldBe(250.00m);
    }

    [Fact]
    public void ReadOwners_optional_reserve_defaults_to_zero_when_absent()
    {
        var result = AppFolioImportCatalog.Owners.Read(
            Csv("Owner ID,Owner Name\nO-2,Linden Properties LLC\n"));

        result.Errors.ShouldBeEmpty();
        result.Rows.ShouldHaveSingleItem().Reserve.ShouldBe(0m);
    }

    [Fact]
    public void ReadOwners_optional_reserve_defaults_to_zero_when_blank()
    {
        var result = AppFolioImportCatalog.Owners.Read(
            Csv("Owner ID,Owner Name,Reserve\nO-2,Linden Properties LLC,\n"));

        result.Errors.ShouldBeEmpty();
        result.Rows.ShouldHaveSingleItem().Reserve.ShouldBe(0m);
    }

    [Fact]
    public void ReadOwners_supplied_non_numeric_reserve_is_a_row_error()
    {
        var result = AppFolioImportCatalog.Owners.Read(
            Csv("Owner ID,Owner Name,Reserve\n" +
                "O-2,Linden Properties LLC,not-money\n" +
                "O-3,Hargrove Family Trust,250.00\n"));

        result.Rows.ShouldHaveSingleItem().ExternalId.ShouldBe("O-3");
        result.Errors.ShouldContain(e => e.RowNumber == 1 && e.Field == "reserve");
    }

    [Fact]
    public void ReadOwners_missing_name_is_a_row_error()
    {
        var result = AppFolioImportCatalog.Owners.Read(
            Csv("Owner ID,Owner Name,Reserve\nO-3,,100.00\n"));

        result.Rows.ShouldBeEmpty();
        result.Errors.ShouldContain(e => e.RowNumber == 1 && e.Field == "name");
    }

    [Fact]
    public void ReadOwners_one_bad_row_does_not_sink_the_batch()
    {
        var result = AppFolioImportCatalog.Owners.Read(
            Csv("Owner ID,Owner Name,Reserve\nO-1,Good Owner,100.00\nO-2,,200.00\n"));

        // Row 1 is valid; row 2 has an empty name and is rejected.
        result.Rows.ShouldHaveSingleItem().Name.ShouldBe("Good Owner");
        result.Errors.ShouldContain(e => e.RowNumber == 2 && e.Field == "name");
    }

    [Fact]
    public void ReadOwners_two_valid_rows_parsed()
    {
        var result = AppFolioImportCatalog.Owners.Read(
            Csv("Owner ID,Owner Name,Reserve\nO-1,Alpha LLC,0\nO-2,Beta LLC,500\n"));

        result.Errors.ShouldBeEmpty();
        result.Rows.Count.ShouldBe(2);
        result.Rows[0].ExternalId.ShouldBe("O-1");
        result.Rows[1].ExternalId.ShouldBe("O-2");
    }

    [Fact]
    public void ReadProperties_happy_path()
    {
        var result = AppFolioImportCatalog.Properties.Read(
            Csv("Property ID,Owner ID,Address\nP-1,O-1,123 Main St\n"));

        result.Errors.ShouldBeEmpty();
        var row = result.Rows.ShouldHaveSingleItem();
        row.ExternalId.ShouldBe("P-1");
        row.ExternalOwnerId.ShouldBe("O-1");
        row.Address.ShouldBe("123 Main St");
    }

    [Fact]
    public void ReadProperties_missing_owner_id_is_a_row_error()
    {
        var result = AppFolioImportCatalog.Properties.Read(
            Csv("Property ID,Owner ID,Address\nP-1,,123 Main St\n"));

        result.Rows.ShouldBeEmpty();
        result.Errors.ShouldContain(e => e.RowNumber == 1 && e.Field == "external_owner_id");
    }

    [Fact]
    public void ReadUnits_happy_path_with_optional_defaults()
    {
        var result = AppFolioImportCatalog.Units.Read(
            Csv("Unit ID,Property ID,Unit\nU-1,P-1,Unit A\n"));

        result.Errors.ShouldBeEmpty();
        var row = result.Rows.ShouldHaveSingleItem();
        row.ExternalId.ShouldBe("U-1");
        row.ExternalPropertyId.ShouldBe("P-1");
        row.Label.ShouldBe("Unit A");
        row.Rent.ShouldBe(0m);       // optional, absent → 0
        row.Status.ShouldBe("vacant"); // optional, absent → "vacant"
    }

    [Fact]
    public void ReadUnits_supplied_non_numeric_rent_is_a_row_error()
    {
        var result = AppFolioImportCatalog.Units.Read(
            Csv("Unit ID,Property ID,Unit,Rent\nU-1,P-1,Unit A,not-money\n"));

        result.Rows.ShouldBeEmpty();
        result.Errors.ShouldContain(e => e.RowNumber == 1 && e.Field == "rent");
    }

    [Fact]
    public void ReadTenantsLeases_happy_path_parses_dates_and_amounts()
    {
        var result = AppFolioImportCatalog.TenantsLeases.Read(
            Csv("Tenant ID,Unit ID,Tenant Name,Lease Start,Lease End,Rent,Deposit,Status\n" +
                "T-1,U-1,Jane Smith,2025-01-01,2026-01-01,1200.00,2400.00,active\n"));

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
        var result = AppFolioImportCatalog.TenantsLeases.Read(
            Csv("Tenant ID,Unit ID,Tenant Name\nT-2,U-2,Bob Jones\n"));

        result.Errors.ShouldBeEmpty();
        var row = result.Rows.ShouldHaveSingleItem();
        row.StartDate.ShouldBeNull();
        row.EndDate.ShouldBeNull();
        row.Rent.ShouldBe(0m);
        row.DepositRequired.ShouldBe(0m);
        row.Status.ShouldBe("active"); // default
    }

    [Fact]
    public void ReadTenantsLeases_blank_optional_values_use_defaults()
    {
        var result = AppFolioImportCatalog.TenantsLeases.Read(
            Csv("Tenant ID,Unit ID,Tenant Name,Lease Start,Lease End,Rent,Deposit\n" +
                "T-2,U-2,Bob Jones,,,,\n"));

        result.Errors.ShouldBeEmpty();
        var row = result.Rows.ShouldHaveSingleItem();
        row.StartDate.ShouldBeNull();
        row.EndDate.ShouldBeNull();
        row.Rent.ShouldBe(0m);
        row.DepositRequired.ShouldBe(0m);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("end")]
    [InlineData("rent")]
    [InlineData("deposit")]
    public void ReadTenantsLeases_supplied_malformed_optional_value_is_a_row_error(string field)
    {
        var values = new Dictionary<string, string>
        {
            ["start"] = "2025-01-01",
            ["end"] = "2026-01-01",
            ["rent"] = "1200.00",
            ["deposit"] = "2400.00",
        };
        values[field] = "malformed";

        var result = AppFolioImportCatalog.TenantsLeases.Read(
            Csv("Tenant ID,Unit ID,Tenant Name,Lease Start,Lease End,Rent,Deposit\n" +
                $"T-1,U-1,Jane Smith,{values["start"]},{values["end"]},{values["rent"]},{values["deposit"]}\n"));

        result.Rows.ShouldBeEmpty();
        result.Errors.ShouldContain(e => e.RowNumber == 1 && e.Field == field);
    }

    [Fact]
    public void ReadHeldPmFees_binds_bank_name_and_amount()
    {
        var csv = "Account ID,Account Name,Held Fees\nB-TRUST,Trust Operating,125.50\n";
        var result = AppFolioImportCatalog.HeldPmFees.Read(Csv(csv));
        result.Errors.ShouldBeEmpty();
        result.Rows.Count.ShouldBe(1);
        result.Rows[0].ShouldBe(new HeldPmFeeRow("B-TRUST", "Trust Operating", 125.50m));
    }

    [Fact]
    public void ReadHeldPmFees_rejects_non_numeric_amount_and_keeps_going()
    {
        var csv = "Account ID,Account Name,Held Fees\nB-1,Trust A,abc\nB-2,Trust B,50.00\n";
        var result = AppFolioImportCatalog.HeldPmFees.Read(Csv(csv));
        result.Errors.Count.ShouldBe(1);
        result.Errors[0].Field.ShouldBe("held_amount");
        result.Rows.Count.ShouldBe(1);
    }

    [Fact]
    public void Catalog_owns_nine_unique_complete_definitions()
    {
        AppFolioImportCatalog.All.Select(definition =>
                (definition.CanonicalToken, definition.PersistedName, definition.Family))
            .ShouldBe(
            [
                ("owners", "Owners", AppFolioImportFamily.Entity),
                ("properties", "Properties", AppFolioImportFamily.Entity),
                ("units", "Units", AppFolioImportFamily.Entity),
                ("tenants_leases", "TenantsLeases", AppFolioImportFamily.Entity),
                ("owner_balances", "OwnerBalances", AppFolioImportFamily.Balance),
                ("deposit_liabilities", "DepositLiabilities", AppFolioImportFamily.Balance),
                ("bank_balances", "BankBalances", AppFolioImportFamily.Balance),
                ("tenant_receivables", "TenantReceivables", AppFolioImportFamily.Balance),
                ("held_pm_fees", "HeldPmFees", AppFolioImportFamily.Balance),
            ]);
        AppFolioImportCatalog.All.Select(definition => definition.PersistedName).Distinct().Count().ShouldBe(9);
        AppFolioImportCatalog.All.Select(definition => definition.CanonicalToken).Distinct().Count().ShouldBe(9);
        AppFolioImportCatalog.All.ShouldAllBe(definition => definition.ProfileId == "appfolio-default");
        AppFolioImportCatalog.All.ShouldAllBe(definition => definition.Profile.Fields.Count > 0);
        AppFolioImportCatalog.ForFamily(AppFolioImportFamily.Entity).Count.ShouldBe(4);
        AppFolioImportCatalog.ForFamily(AppFolioImportFamily.Balance).Count.ShouldBe(5);
    }

    [Fact]
    public void Every_definition_reports_missing_required_headers_through_its_typed_interface()
    {
        AssertMissingRequiredHeader(AppFolioImportCatalog.Owners);
        AssertMissingRequiredHeader(AppFolioImportCatalog.Properties);
        AssertMissingRequiredHeader(AppFolioImportCatalog.Units);
        AssertMissingRequiredHeader(AppFolioImportCatalog.TenantsLeases);
        AssertMissingRequiredHeader(AppFolioImportCatalog.OwnerBalances);
        AssertMissingRequiredHeader(AppFolioImportCatalog.DepositLiabilities);
        AssertMissingRequiredHeader(AppFolioImportCatalog.BankBalances);
        AssertMissingRequiredHeader(AppFolioImportCatalog.TenantReceivables);
        AssertMissingRequiredHeader(AppFolioImportCatalog.HeldPmFees);
    }

    [Theory]
    [InlineData("owner_balances")]
    [InlineData("OWNERBALANCES")]
    [InlineData("_OwNeR__BaLaNcEs_")]
    public void Lookup_preserves_case_and_underscore_insensitive_aliases(string token)
    {
        AppFolioImportCatalog.TryResolve(
                token,
                AppFolioImportFamily.Balance,
                out var definition)
            .ShouldBeTrue();
        definition.ShouldBeSameAs(AppFolioImportCatalog.OwnerBalances);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("owners")]
    [InlineData("not_a_kind")]
    public void Balance_lookup_rejects_numeric_wrong_family_and_unknown_tokens(string token)
    {
        AppFolioImportCatalog.TryResolve(
                token,
                AppFolioImportFamily.Balance,
                out _)
            .ShouldBeFalse();
    }

    private static void AssertMissingRequiredHeader<TRow>(AppFolioImportDefinition<TRow> definition)
        where TRow : class
    {
        var result = definition.Read(Csv("Unexpected\nvalue\n"));

        result.Rows.ShouldBeEmpty();
        result.Errors.ShouldNotBeEmpty();
        result.Errors.ShouldAllBe(error => error.RowNumber == 0 && error.Reason.Contains("required column"));
    }
}
