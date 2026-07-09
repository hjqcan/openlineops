using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class PostgresRuntimeSessionRepositoryTests
{
    [Fact]
    public void ResolvePostgreSqlConnectionStringRequiresExplicitConnectionString()
    {
        var options = new RuntimeSessionPersistenceOptions
        {
            Provider = RuntimeSessionPersistenceProviders.PostgreSql,
            ConnectionString = " "
        };

        var exception = Assert.Throws<InvalidOperationException>(options.ResolvePostgreSqlConnectionString);

        Assert.Equal("PostgreSQL runtime session persistence requires ConnectionString.", exception.Message);
    }

    [Fact]
    public void ResolvePostgreSqlConnectionStringTrimsConfiguredConnectionString()
    {
        var options = new RuntimeSessionPersistenceOptions
        {
            Provider = RuntimeSessionPersistenceProviders.PostgreSql,
            ConnectionString = " Host=localhost;Username=openlineops;Password=openlineops;Database=openlineops "
        };

        var connectionString = options.ResolvePostgreSqlConnectionString();

        Assert.Equal("Host=localhost;Username=openlineops;Password=openlineops;Database=openlineops", connectionString);
    }

    [Fact]
    public void ConstructorCreatesRepositoryWithoutOpeningDatabaseConnection()
    {
        using var repository = new PostgresRuntimeSessionRepository(
            "Host=localhost;Username=openlineops;Password=openlineops;Database=openlineops");

        Assert.NotNull(repository);
    }
}
