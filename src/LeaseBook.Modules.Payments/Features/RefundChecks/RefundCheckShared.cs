using LeaseBook.Modules.Payments.Contracts;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Payments.Features.RefundChecks;

public sealed record RefundCheckView(
    Guid Id, Guid TenantId, Guid BankAccountId, int CheckNumber, string Source, decimal Amount, DateOnly IssueDate,
    string PayeeName, string AddressLine1, string? AddressLine2, string City, string State, string PostalCode,
    string? Memo, Guid EntryId, string Status, Guid? VoidEntryId, int PrintCount, DateTime? LastPrintedAt,
    DateTime CreatedAt);

/// <summary>
/// A refund-check rejection with a stable code the host maps to 409 (ADR-025): a number already used on
/// the bank, a retried key carrying different data, or printing a voided check.
/// </summary>
public sealed class RefundCheckConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    public static RefundCheckConflictException NumberTaken() => new(
        "check_number_taken", "That check number has already been used on this bank account.");

    public static RefundCheckConflictException KeyReused() => new(
        "refund_check_conflict", "This request conflicts with a check that was already issued.");

    public static RefundCheckConflictException Voided() => new(
        "refund_check_voided", "This check has been voided and cannot be printed.");
}

internal static class RefundCheckReads
{
    /// <summary>
    /// Serializes refund-check issue, void and print within an organization, so the idempotency-key and
    /// check-number checks cannot race and a print cannot land on a check being voided. Taken before
    /// Accounting's posting lock, never after it.
    /// </summary>
    public static async Task LockAsync(DbContext db, IOrgContext org, CancellationToken ct)
    {
        var orgId = org.OrgId ?? throw new InvalidOperationException("Refund checks require an organization context.");
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended('lb:refund-check:' || {orgId.ToString()}, 0))", ct);
    }

    public static async Task<IReadOnlyList<RefundCheckView>> ViewsAsync(
        DbContext db, IRefundCheckLedger ledger, IReadOnlyList<RefundCheck> checks, CancellationToken ct)
    {
        if (checks.Count == 0)
        {
            return [];
        }

        var ids = checks.Select(c => c.Id).ToArray();
        var prints = await db.Set<RefundCheckPrint>().AsNoTracking()
            .Where(p => ids.Contains(p.CheckId))
            .GroupBy(p => p.CheckId)
            .Select(g => new { CheckId = g.Key, Count = g.Count(), Last = g.Max(p => p.CreatedAt) })
            .ToDictionaryAsync(p => p.CheckId, ct);
        var statuses = await ledger.GetStatusesAsync(checks.Select(c => c.EntryId).ToArray(), ct);

        return checks.Select(c =>
        {
            var status = statuses.GetValueOrDefault(c.EntryId);
            var printed = prints.GetValueOrDefault(c.Id);
            return new RefundCheckView(c.Id, c.TenantId, c.BankAccountId, c.CheckNumber, c.Source, c.Amount, c.IssueDate,
                c.PayeeName, c.AddressLine1, c.AddressLine2, c.City, c.State, c.PostalCode, c.Memo, c.EntryId,
                status?.Status ?? "outstanding", status?.VoidEntryId, printed?.Count ?? 0, printed?.Last, c.CreatedAt);
        }).ToArray();
    }
}
