using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Accounting.Features.Posting.Events;

// The business-event catalog (§C.3). One record per row; each maps to a balanced, per-basis posting in
// AccountingEventService. Dimensions are bare uuids in M1 (P26). Amounts are taken as given (P28) — no
// percentage/fee math here (that is M6). Records are public: the seeder, M3 composer, and M6 runs
// construct them.

/// <summary>Late/maintenance-recharge/other fee kind; becomes the entry's <c>event_subtype</c>.</summary>
public enum FeeKind
{
    Late,
    MaintenanceRecharge,
    Other,
}

/// <summary>Tender of a tenant payment; becomes the entry's <c>event_subtype</c>.</summary>
public enum PaymentMethod
{
    Ach,
    Card,
    Check,
    Cash,
}

/// <summary>Whether an applied deposit becomes owner income (damages) or clears outstanding charges.</summary>
public enum DepositApplication
{
    ToOwnerIncome,
    AgainstCharges,
}

/// <summary>Which held liability a refund draws down.</summary>
public enum RefundSource
{
    Prepayments,
    Deposits,
}

/// <summary>Rent accrues: receivable up, owner income up (accrual only).</summary>
public sealed record RentCharged(
    Guid TenantId, Guid PropertyId, Guid OwnerId, Guid? UnitId,
    Money Amount, DateOnly Date, string Description, string? SourceRef = null) : AccountingEvent;

/// <summary>A non-rent charge (late fee, maintenance recharge, …); same shape as rent (M1: accrues to owner).</summary>
public sealed record FeeCharged(
    Guid TenantId, Guid PropertyId, Guid OwnerId, Guid? UnitId,
    Money Amount, DateOnly Date, FeeKind Kind, string Description, string? SourceRef = null) : AccountingEvent;

/// <summary>A goodwill credit reduces what the tenant owes and the owner's accrued income.</summary>
public sealed record CreditIssued(
    Guid TenantId, Guid PropertyId, Guid OwnerId,
    Money Amount, DateOnly Date, string Reason, string? SourceRef = null) : AccountingEvent;

/// <summary>A tenant payment into a trust bank; auto-splits receivable vs. prepayment (P31).</summary>
public sealed record PaymentReceived(
    Guid TenantId, Guid PropertyId, Guid OwnerId,
    Money Amount, DateOnly Date, PaymentMethod Method, Guid BankAccountId,
    string Description, string? SourceRef = null) : AccountingEvent;

/// <summary>A security deposit into a deposit-trust bank — a liability, never income.</summary>
public sealed record DepositCollected(
    Guid TenantId, Guid PropertyId, Guid OwnerId,
    Money Amount, DateOnly Date, Guid DepositBankId, string Description, string? SourceRef = null) : AccountingEvent;

/// <summary>A prepayment into a trust bank — a liability until applied.</summary>
public sealed record PrepaymentReceived(
    Guid TenantId, Guid PropertyId, Guid OwnerId,
    Money Amount, DateOnly Date, Guid BankAccountId, string Description, string? SourceRef = null) : AccountingEvent;

/// <summary>Applies a held deposit (damages → owner income, or against the tenant's charges).</summary>
public sealed record DepositApplied(
    Guid TenantId, Guid PropertyId, Guid OwnerId,
    Money Amount, DateOnly Date, Guid DepositBankId, Guid OperatingBankId,
    DepositApplication Target, string Description, string? SourceRef = null) : AccountingEvent;

/// <summary>Applies a held prepayment to the tenant's charges (no bank movement).</summary>
public sealed record PrepaymentApplied(
    Guid TenantId, Guid PropertyId, Guid OwnerId,
    Money Amount, DateOnly Date, Guid BankAccountId, string Description, string? SourceRef = null) : AccountingEvent;

/// <summary>The PM's management fee: owner income down, PM income up (held in trust until swept).</summary>
public sealed record ManagementFeeAssessed(
    Guid OwnerId, Guid? PropertyId,
    Money Amount, DateOnly Date, Guid OperatingBankId, string Description, string? SourceRef = null) : AccountingEvent;

/// <summary>Sweeps held PM fees from the operating trust bank to the PM's own operating bank.</summary>
public sealed record PMFeesSwept(
    Money Amount, DateOnly Date, Guid OperatingBankId, Guid PmBankId,
    string Description, string? SourceRef = null) : AccountingEvent;

/// <summary>An owner deposits their own funds into a trust bank.</summary>
public sealed record OwnerContribution(
    Guid OwnerId, Guid? PropertyId,
    Money Amount, DateOnly Date, Guid BankAccountId, string Description, string? SourceRef = null) : AccountingEvent;

/// <summary>A disbursement of an owner's trust funds to the owner (guarded by the reserve floor).</summary>
public sealed record OwnerDisbursed(
    Guid OwnerId, Money Amount, DateOnly Date, Guid BankAccountId,
    string Description, string? SourceRef = null, Money Reserve = default) : AccountingEvent;

/// <summary>A vendor payment out of an owner's trust funds (guarded by the reserve floor).</summary>
public sealed record VendorPaid(
    Guid OwnerId, Guid PropertyId, Money Amount, DateOnly Date, Guid BankAccountId, string Payee,
    string Description, string? SourceRef = null, Money Reserve = default) : AccountingEvent;

/// <summary>A refund of a held prepayment or deposit back to the tenant.</summary>
public sealed record RefundIssued(
    Guid TenantId, Money Amount, DateOnly Date, Guid BankAccountId, RefundSource Source,
    string Description, string? SourceRef = null) : AccountingEvent;
