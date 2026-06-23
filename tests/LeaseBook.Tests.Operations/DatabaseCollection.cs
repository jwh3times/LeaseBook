using LeaseBook.Tests.Common;

namespace LeaseBook.Tests.Operations;

/// <summary>
/// Shares one <see cref="PostgresFixture"/> (one Postgres 18 container) across all DB-backed tests in
/// the Operations suite. xUnit collections are per-assembly, so this declaration is separate from
/// the other suites' even though all bind the same fixture type from LeaseBook.Tests.Common.
/// </summary>
[CollectionDefinition(nameof(DatabaseCollection))]
public sealed class DatabaseCollection : ICollectionFixture<PostgresFixture>
{
}
