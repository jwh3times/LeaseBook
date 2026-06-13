using LeaseBook.Tests.Common;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// Shares one <see cref="PostgresFixture"/> (one Postgres 18 container) across all DB-backed tests in
/// the accounting suite. xUnit collections are per-assembly, so this declaration is separate from the
/// integration suite's even though both bind the same fixture type from LeaseBook.Tests.Common.
/// </summary>
[CollectionDefinition(nameof(DatabaseCollection))]
public sealed class DatabaseCollection : ICollectionFixture<PostgresFixture>
{
}
