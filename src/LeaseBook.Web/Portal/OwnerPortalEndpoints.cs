namespace LeaseBook.Web.Portal;

public sealed record OwnerPortalDisbursement(DateOnly Date, decimal Amount, bool IsVoided, bool IsReversal);
public sealed record OwnerPortalActivityRow(
    DateOnly Date, string Category, string? PropertyAddress, decimal Amount, decimal Balance, bool IsVoided, bool IsReversal);
public sealed record OwnerPortalSummary(
    string OwnerName, decimal Balance, string Basis,
    IReadOnlyList<OwnerPortalDisbursement> Disbursements, IReadOnlyList<OwnerPortalActivityRow> Activity);
public sealed record OwnerPortalStatement(
    Guid Id, int PeriodYear, int PeriodMonth, string? Basis, string Scope, decimal? EndingBalance, DateTime IssuedAt);
public sealed record OwnerPortalStatements(IReadOnlyList<OwnerPortalStatement> Statements);

public static class OwnerPortalProjection
{
    public static string Category(string eventType, bool isReversal) => eventType;
}
