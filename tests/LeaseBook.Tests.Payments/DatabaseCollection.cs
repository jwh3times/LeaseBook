using LeaseBook.Tests.Common;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// Shares one <see cref="PostgresFixture"/> (one Postgres 18 container) across the payments suite.
/// xUnit collections are per-assembly, so this is declared here as it is in the accounting suite.
/// </summary>
[CollectionDefinition(nameof(DatabaseCollection))]
public sealed class DatabaseCollection : ICollectionFixture<PostgresFixture>
{
}
