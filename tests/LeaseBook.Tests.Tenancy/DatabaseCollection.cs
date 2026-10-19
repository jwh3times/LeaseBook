using LeaseBook.Tests.Common;

namespace LeaseBook.Tests.Tenancy;

/// <summary>
/// Shares one <see cref="PostgresFixture"/> (one Postgres 18 container) across the tenancy suite.
/// xUnit collections are per-assembly, so this is declared here as it is in the other suites.
/// </summary>
[CollectionDefinition(nameof(DatabaseCollection))]
public sealed class DatabaseCollection : ICollectionFixture<PostgresFixture>
{
}
