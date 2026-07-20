using System.Linq.Expressions;
using FluentValidation;
using FluentValidation.Results;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Accounting.Features.LedgerPosting;

/// <summary>The id of the entry a ledger-posting command posted (§C.1). The composer uses it to flash
/// the new row without navigation (P59).</summary>
public sealed record PostResult(Guid EntryId);

/// <summary>
/// Shared vocabulary + helpers for the M3 ledger-posting commands (P50/P58). The composer sends string
/// discriminators (matching the §C.1 JSON contract); validators check them against these sets and the
/// handlers map them to the engine's enums — one home for the allowed values, mirroring Directory's
/// status-converter pattern. The receivable guard, idempotency, and balancing all stay in the engine
/// (M3-E1): these commands only resolve dimensions and dispatch the existing event catalog.
/// </summary>
internal static class LedgerPostingMaps
{
    public static readonly IReadOnlyDictionary<string, PaymentMethod> Methods =
        new Dictionary<string, PaymentMethod>(StringComparer.OrdinalIgnoreCase)
        {
            ["ach"] = PaymentMethod.Ach,
            ["card"] = PaymentMethod.Card,
            ["check"] = PaymentMethod.Check,
            ["cash"] = PaymentMethod.Cash,
        };

    public static readonly IReadOnlyDictionary<string, DepositApplication> DepositTargets =
        new Dictionary<string, DepositApplication>(StringComparer.OrdinalIgnoreCase)
        {
            ["to-owner-income"] = DepositApplication.ToOwnerIncome,
            ["against-charges"] = DepositApplication.AgainstCharges,
        };

    /// <summary>Charge kinds: <c>rent</c> posts <c>RentCharged</c>; the rest post <c>FeeCharged</c> with this fee kind.</summary>
    public static readonly IReadOnlyDictionary<string, FeeKind?> ChargeKinds =
        new Dictionary<string, FeeKind?>(StringComparer.OrdinalIgnoreCase)
        {
            ["rent"] = null,
            ["late"] = FeeKind.Late,
            ["maintenance-recharge"] = FeeKind.MaintenanceRecharge,
            ["other"] = FeeKind.Other,
        };

    /// <summary>A strictly-positive money amount with at most 2 decimal places (the Money gate, P28).</summary>
    public static void RuleForAmount<T>(AbstractValidator<T> validator, Expression<Func<T, decimal>> amount)
        where T : class =>
        validator.RuleFor(amount).Must(a => a > 0m && decimal.Round(a, 2) == a)
            .WithMessage("Amount must be a positive value with at most 2 decimal places.");

    /// <summary>
    /// Resolve the tenant's posting dimensions from the active lease, or reject (no active lease ⇒ 400
    /// validation, never a silent default — P58 / M3-E3). The cross-module read rides the ambient RLS
    /// transaction through the host adapter, so a tenant the caller's org cannot see resolves to null.
    /// </summary>
    public static async Task<TenantPostingDimensions> ResolveAsync(
        ITenantPostingDimensions dimensions, Guid tenantId, CancellationToken ct) =>
        await dimensions.GetAsync(tenantId, ct)
        ?? throw new ValidationException(
        [
            new ValidationFailure(
                "tenantId", "This tenant has no active lease, so the posting's owner/property cannot be resolved."),
        ]);

    public static Money Money(decimal amount) => new(amount);
}
