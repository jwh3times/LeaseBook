using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Payments.Domain;

/// <summary>
/// A refund check issued to a tenant (#473). Written once at issue, in the same transaction as the
/// <c>RefundIssued</c> entry it names, and never updated: status (outstanding / cleared / reconciled /
/// voided) is derived from the ledger, and each print is its own <see cref="RefundCheckPrint"/> row.
/// Payee name and mailing address live only here, never in journal text.
/// </summary>
public sealed class RefundCheck : IOrgScoped
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }

    /// <summary>The client's idempotency key: a retried issue returns this check instead of posting again.</summary>
    public Guid Key { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The bank the refund was drawn on — derived from the held liability, never chosen.</summary>
    public Guid BankAccountId { get; set; }

    /// <summary>Unique per bank account across issued and voided checks, so every number stays accounted for.</summary>
    public int CheckNumber { get; set; }

    public string Source { get; set; } = "";
    public decimal Amount { get; set; }
    public DateOnly IssueDate { get; set; }
    public string PayeeName { get; set; } = "";
    public string AddressLine1 { get; set; } = "";
    public string? AddressLine2 { get; set; }
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public string PostalCode { get; set; } = "";

    /// <summary>Printed on the check's memo line and stubs.</summary>
    public string? Memo { get; set; }

    public Guid EntryId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>One rendering of a check's PDF. Append-only; the audit row records who printed it.</summary>
public sealed class RefundCheckPrint : IOrgScoped
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid CheckId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Per-bank-account print calibration for pre-printed check stock: the shift, in points, applied to every
/// printed field so they land in the stock's boxes on that account's printer.
/// </summary>
public sealed class CheckPrintSetting : IOrgScoped
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid BankAccountId { get; set; }
    public decimal OffsetXPoints { get; set; }
    public decimal OffsetYPoints { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A bank account's MICR details for printing complete checks on blank stock (#474). The routing number
/// and the bank's On-Us field are encrypted at rest by the host, never leave the server in full (reads
/// carry the last four digits), and never enter an audit snapshot. <see cref="OnUsAccountNumber"/> is the
/// On-Us field exactly as the bank's specification sheet prints it — the account number with any codes,
/// dashes and On-Us symbols — and its name keeps it on the audit layer's secret-name list.
/// </summary>
public sealed class BankMicrProfile : IOrgScoped
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid BankAccountId { get; set; }
    public string StockKind { get; set; } = CheckStockKinds.PrePrinted;
    public string? RoutingNumber { get; set; }
    public string? OnUsAccountNumber { get; set; }
    public decimal MicrOffsetXPoints { get; set; }
    public decimal MicrOffsetYPoints { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>What a bank account's checks are printed on.</summary>
public static class CheckStockKinds
{
    /// <summary>Stock that already carries the MICR line, bank and organization blocks (#473).</summary>
    public const string PrePrinted = "preprinted";

    /// <summary>Blank stock: LeaseBook prints the whole check, MICR line included (#474).</summary>
    public const string Blank = "blank";

    public static readonly IReadOnlyList<string> All = [PrePrinted, Blank];
}
