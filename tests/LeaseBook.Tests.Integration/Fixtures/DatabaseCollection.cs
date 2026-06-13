using LeaseBook.Tests.Common;

namespace LeaseBook.Tests.Integration.Fixtures;

/// <summary>
/// Shares one <see cref="PostgresFixture"/> (one container) across all DB-backed tests in this
/// assembly. The fixture now lives in LeaseBook.Tests.Common; xUnit collections are per-assembly,
/// so this definition stays here (LeaseBook.Tests.Accounting declares its own).
/// </summary>
[CollectionDefinition(nameof(DatabaseCollection))]
public sealed class DatabaseCollection : ICollectionFixture<PostgresFixture>
{
}
