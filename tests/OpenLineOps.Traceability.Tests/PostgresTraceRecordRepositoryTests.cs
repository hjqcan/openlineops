using OpenLineOps.Traceability.Infrastructure.Persistence;

namespace OpenLineOps.Traceability.Tests;

public sealed class PostgresTraceRecordRepositoryTests
{
    [Fact]
    public void ResolvePostgreSqlConnectionStringRequiresExplicitConnectionString()
    {
        var options = new TraceRecordPersistenceOptions
        {
            Provider = TraceRecordPersistenceProviders.PostgreSql,
            ConnectionString = " "
        };

        var exception = Assert.Throws<InvalidOperationException>(options.ResolvePostgreSqlConnectionString);

        Assert.Equal("PostgreSQL traceability persistence requires ConnectionString.", exception.Message);
    }

    [Fact]
    public void ResolvePostgreSqlConnectionStringTrimsConfiguredConnectionString()
    {
        var options = new TraceRecordPersistenceOptions
        {
            Provider = TraceRecordPersistenceProviders.PostgreSql,
            ConnectionString = " Host=localhost;Username=openlineops;Password=openlineops;Database=openlineops "
        };

        var connectionString = options.ResolvePostgreSqlConnectionString();

        Assert.Equal("Host=localhost;Username=openlineops;Password=openlineops;Database=openlineops", connectionString);
    }

    [Fact]
    public void ConstructorCreatesRepositoryWithoutOpeningDatabaseConnection()
    {
        using var repository = new PostgresTraceRecordRepository(
            "Host=localhost;Username=openlineops;Password=openlineops;Database=openlineops");

        Assert.NotNull(repository);
    }
}
