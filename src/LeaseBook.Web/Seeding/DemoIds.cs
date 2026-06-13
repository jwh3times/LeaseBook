namespace LeaseBook.Web.Seeding;

/// <summary>
/// Stable GUIDs for every demo-dataset entity (§C.8). Checked in so the journal seed is deterministic
/// and re-runs are idempotent. <b>M2 reuses these exact ids</b> for the directory rows (owners,
/// properties, tenants) it adds — the journal lines already carry them as dimensions (P26), so the M2
/// migration just adds the FK targets. Synthetic aggregates stand in for entities the prototype lists
/// only in summary (the 15 unlisted owners; unattributed deposit liability).
/// </summary>
public static class DemoIds
{
    // Banks.
    public static readonly Guid OperBank = new("01923000-0000-7000-8000-00000000ba01");
    public static readonly Guid DepositBank = new("01923000-0000-7000-8000-00000000ba02");
    public static readonly Guid MgmtBank = new("01923000-0000-7000-8000-00000000ba03");

    // Owners o1–o8.
    public static readonly Guid O1 = new("01923000-0000-7000-8000-000000000a01");
    public static readonly Guid O2 = new("01923000-0000-7000-8000-000000000a02");
    public static readonly Guid O3 = new("01923000-0000-7000-8000-000000000a03");
    public static readonly Guid O4 = new("01923000-0000-7000-8000-000000000a04");
    public static readonly Guid O5 = new("01923000-0000-7000-8000-000000000a05");
    public static readonly Guid O6 = new("01923000-0000-7000-8000-000000000a06");
    public static readonly Guid O7 = new("01923000-0000-7000-8000-000000000a07");
    public static readonly Guid O8 = new("01923000-0000-7000-8000-000000000a08");

    // Properties p1–p6.
    public static readonly Guid P1 = new("01923000-0000-7000-8000-000000000b01");
    public static readonly Guid P2 = new("01923000-0000-7000-8000-000000000b02");
    public static readonly Guid P3 = new("01923000-0000-7000-8000-000000000b03");
    public static readonly Guid P4 = new("01923000-0000-7000-8000-000000000b04");
    public static readonly Guid P5 = new("01923000-0000-7000-8000-000000000b05");
    public static readonly Guid P6 = new("01923000-0000-7000-8000-000000000b06");

    // Tenants t1–t7.
    public static readonly Guid T1 = new("01923000-0000-7000-8000-000000000c01");
    public static readonly Guid T2 = new("01923000-0000-7000-8000-000000000c02");
    public static readonly Guid T3 = new("01923000-0000-7000-8000-000000000c03");
    public static readonly Guid T4 = new("01923000-0000-7000-8000-000000000c04");
    public static readonly Guid T5 = new("01923000-0000-7000-8000-000000000c05");
    public static readonly Guid T6 = new("01923000-0000-7000-8000-000000000c06");
    public static readonly Guid T7 = new("01923000-0000-7000-8000-000000000c07");

    // Statement-only tenants (appear in the May statement, not in tenants[]); seeded in M5, not M1.
    public static readonly Guid TOkonkwo = new("01923000-0000-7000-8000-000000000c91");
    public static readonly Guid TLiu = new("01923000-0000-7000-8000-000000000c92");

    // Synthetic aggregates (clearly not real entities).
    public static readonly Guid AggregateOwners = new("01923000-0000-7000-8000-000000000a99");
    public static readonly Guid AggregateDepositsUnattributed = new("01923000-0000-7000-8000-000000000dff");

    public static readonly Guid AggDepO1 = new("01923000-0000-7000-8000-000000000d01");
    public static readonly Guid AggDepO2 = new("01923000-0000-7000-8000-000000000d02");
    public static readonly Guid AggDepO3 = new("01923000-0000-7000-8000-000000000d03");
    public static readonly Guid AggDepO4 = new("01923000-0000-7000-8000-000000000d04");
    public static readonly Guid AggDepO5 = new("01923000-0000-7000-8000-000000000d05");
    public static readonly Guid AggDepO6 = new("01923000-0000-7000-8000-000000000d06");
    public static readonly Guid AggDepO7 = new("01923000-0000-7000-8000-000000000d07");
    public static readonly Guid AggDepO8 = new("01923000-0000-7000-8000-000000000d08");
}
