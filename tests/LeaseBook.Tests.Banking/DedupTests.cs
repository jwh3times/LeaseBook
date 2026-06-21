using LeaseBook.Modules.Banking.Import;
using Shouldly;

namespace LeaseBook.Tests.Banking;

/// <summary>
/// The dedup hash (P67): a stable fingerprint of <c>(statement_date, amount, normalized_description)</c> so a
/// re-imported statement line collides with its prior copy and is skipped, while a genuinely different line
/// (changed amount/date/payee) hashes differently and imports as new. Normalization absorbs trivial
/// reformatting (case, whitespace runs) that banks vary between exports.
/// </summary>
public sealed class DedupTests
{
    private static readonly DateOnly Feb1 = new(2026, 2, 1);

    [Fact]
    public void Identical_lines_hash_identically()
    {
        DedupHash.Compute(Feb1, 1500.00m, "ACH DEPOSIT RENT")
            .ShouldBe(DedupHash.Compute(Feb1, 1500.00m, "ACH DEPOSIT RENT"));
    }

    [Fact]
    public void A_changed_amount_hashes_differently()
    {
        DedupHash.Compute(Feb1, 1500.00m, "ACH DEPOSIT RENT")
            .ShouldNotBe(DedupHash.Compute(Feb1, 1500.01m, "ACH DEPOSIT RENT"));
    }

    [Fact]
    public void A_changed_date_hashes_differently()
    {
        DedupHash.Compute(Feb1, 1500.00m, "ACH DEPOSIT RENT")
            .ShouldNotBe(DedupHash.Compute(new DateOnly(2026, 2, 2), 1500.00m, "ACH DEPOSIT RENT"));
    }

    [Fact]
    public void Normalization_absorbs_case_and_whitespace_differences()
    {
        DedupHash.Compute(Feb1, 1500.00m, "ACH DEPOSIT RENT")
            .ShouldBe(DedupHash.Compute(Feb1, 1500.00m, "  ach   deposit  rent "));
    }
}
