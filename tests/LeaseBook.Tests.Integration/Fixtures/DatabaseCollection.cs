namespace LeaseBook.Tests.Integration.Fixtures;

/// <summary>Shares one <see cref="PostgresFixture"/> (one container) across all DB-backed tests.</summary>
[CollectionDefinition(nameof(DatabaseCollection))]
public sealed class DatabaseCollection : ICollectionFixture<PostgresFixture>
{
}
