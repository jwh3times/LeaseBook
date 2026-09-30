using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.Modules.Directory.Features.Settings;
using LeaseBook.Modules.Reporting.Features.IssuedStatements;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.Extensions.Logging;

namespace LeaseBook.Web.Portal;

/// <summary>
/// Composes the owner portal's responses from the real module reads, for an owner the authorization
/// handler has already resolved. Every figure is verbatim from Accounting — no balance arithmetic here —
/// and every field is an explicit allow-list projection: no entry ids, source references, descriptions,
/// bank details, tenant identities or PM-income figures ever reach a response.
/// </summary>
public sealed class OwnerPortalReader(ISender sender, ILogger<OwnerPortalReader> logger)
{
    internal static readonly EventId DocumentUnavailable = new(4700, "OwnerStatementDocumentUnavailable");

    public async Task<OwnerPortalSummary> GetSummaryAsync(OwnerIdentity owner, CancellationToken ct)
    {
        // Read-only: the owner persona may read org_settings but never write it (ADR-048), so a
        // missing row resolves to its defaults rather than being created here.
        var basis = (await sender.Query(new GetOrgSettings(CreateIfMissing: false), ct)).AccountingBasis;
        // Owner equity only: the ledger reads owner_equity lines, so deposits (a tenant liability) are
        // not in it, and its balance is exactly GetOwnerBalances' Operating column for this owner.
        var ledger = await sender.Query(new GetOwnerLedger(owner.OwnerId, basis), ct);
        var addresses = await AddressesAsync(ledger.Rows.Select(r => r.PropertyId), ct);

        var activity = ledger.Rows.Select(r => new OwnerPortalActivityRow(
            r.Date,
            OwnerPortalProjection.Category(r.EventType, r.ReversesEntryId is not null),
            r.PropertyId is { } propertyId ? addresses.GetValueOrDefault(propertyId) : null,
            r.Amount,
            r.Balance,
            r.IsVoided,
            r.ReversesEntryId is not null)).ToArray();

        return new OwnerPortalSummary(
            owner.DisplayName, ledger.Balance, basis, OwnerPortalProjection.Disbursements(ledger.Rows), activity);
    }

    public async Task<OwnerPortalStatements> GetStatementsAsync(OwnerIdentity owner, CancellationToken ct)
    {
        var issued = await sender.Query(new GetIssuedOwnerStatements(owner.OwnerId), ct);
        var addresses = await AddressesAsync(issued.Select(s => s.PropertyId), ct);
        return new OwnerPortalStatements(issued.Select(s => new OwnerPortalStatement(
            s.Id,
            s.PeriodYear,
            s.PeriodMonth,
            s.Basis,
            s.PropertyId is { } propertyId
                ? addresses.GetValueOrDefault(propertyId, OwnerPortalProjection.UnnamedPropertyScope)
                : OwnerPortalProjection.AllPropertiesScope,
            s.EndingBalance,
            s.IssuedAt)).ToArray());
    }

    public async Task<IssuedStatementDocument> GetDocumentAsync(OwnerIdentity owner, Guid artifactId, CancellationToken ct)
    {
        var document = await sender.Query(new GetIssuedOwnerStatementDocument(owner.OwnerId, artifactId), ct);
        if (document.Status == IssuedStatementDocumentStatus.Unavailable)
        {
            // The row says the owner was issued this document, and the store cannot produce it. That is an
            // operator problem (lost or unmigrated artifact bytes), not the owner's, so it is logged here.
            logger.LogWarning(DocumentUnavailable,
                "Issued statement artifact {ArtifactId} for owner {OwnerId} (period {Year}-{Month:D2}) has no stored "
                + "bytes; the owner portal reported it unavailable.",
                artifactId, owner.OwnerId, document.PeriodYear, document.PeriodMonth);
        }

        return document;
    }

    private async Task<IReadOnlyDictionary<Guid, string>> AddressesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        // Only property ids already present on the owner's own rows are looked up, so a property the
        // owner has since sold keeps its history and no other property is ever named.
        var wanted = ids.OfType<Guid>().Distinct().ToArray();
        return wanted.Length == 0
            ? new Dictionary<Guid, string>()
            : await sender.Query(new GetPropertyAddresses(wanted), ct);
    }
}

/// <summary>The owner-facing vocabulary. Pure, so the allow-list is testable without a host.</summary>
public static class OwnerPortalProjection
{
    public const string AllPropertiesScope = "All properties";
    public const string UnnamedPropertyScope = "Single property";

    /// <summary>
    /// Owner copy for an Accounting event type. An allow-list, not a pass-through: an event type added
    /// later falls back to a neutral label rather than surfacing its internal name.
    /// </summary>
    public static string Category(string eventType, bool isReversal) => isReversal
        ? "Reversal"
        : eventType switch
        {
            "RentCharged" => "Rent",
            "FeeCharged" => "Tenant charge",
            "PaymentReceived" => "Tenant payment",
            "CreditIssued" => "Tenant credit",
            "DepositApplied" => "Applied deposit",
            "PrepaymentApplied" => "Applied prepayment",
            "ManagementFeeAssessed" => "Management fee",
            "VendorPaid" => "Vendor payment",
            "OwnerContribution" => "Owner contribution",
            "OwnerDisbursed" => "Owner disbursement",
            "OpeningBalance" or "BalanceForward" => "Opening balance",
            _ => "Adjustment",
        };

    /// <summary>
    /// Disbursement history from the owner ledger's <c>OwnerDisbursed</c> rows and the reversals that
    /// void them, in ledger order. Amount is the owner-equity movement negated: positive when paid out,
    /// negative for the reversal that returned it.
    /// </summary>
    public static IReadOnlyList<OwnerPortalDisbursement> Disbursements(IReadOnlyList<OwnerLedgerRow> rows)
    {
        var disbursed = rows
            .Where(r => r.EventType == "OwnerDisbursed" && r.ReversesEntryId is null)
            .Select(r => r.EntryId)
            .ToHashSet();
        return rows
            .Where(r => disbursed.Contains(r.EntryId) || (r.ReversesEntryId is { } original && disbursed.Contains(original)))
            .GroupBy(r => r.EntryId)
            .Select(g => new OwnerPortalDisbursement(
                g.First().Date, -g.Sum(r => r.Amount), g.First().IsVoided, g.First().ReversesEntryId is not null))
            .ToArray();
    }
}
