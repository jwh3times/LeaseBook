namespace LeaseBook.SharedKernel;

/// <summary>
/// App-side primary-key generation (P6): UUIDv7 — time-ordered, index-friendly, generated in
/// application code rather than by a database default.
/// </summary>
public static class UuidV7
{
    public static Guid NewId() => Guid.CreateVersion7();
}
