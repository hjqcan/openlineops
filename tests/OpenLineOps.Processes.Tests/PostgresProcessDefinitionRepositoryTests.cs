using OpenLineOps.Processes.Infrastructure.Persistence;

namespace OpenLineOps.Processes.Tests;

public sealed class PostgresProcessDefinitionRepositoryTests
{
    [Fact]
    public void ResolvePostgreSqlConnectionStringRequiresExplicitConnectionString()
    {
        var options = new ProcessDefinitionPersistenceOptions
        {
            Provider = ProcessDefinitionPersistenceProviders.PostgreSql,
            ConnectionString = " "
        };

        var exception = Assert.Throws<InvalidOperationException>(options.ResolvePostgreSqlConnectionString);

        Assert.Equal("PostgreSQL process definition persistence requires ConnectionString.", exception.Message);
    }

    [Fact]
    public void ResolvePostgreSqlConnectionStringTrimsConfiguredConnectionString()
    {
        var options = new ProcessDefinitionPersistenceOptions
        {
            Provider = ProcessDefinitionPersistenceProviders.PostgreSql,
            ConnectionString = " Host=localhost;Username=openlineops;Password=openlineops;Database=openlineops "
        };

        var connectionString = options.ResolvePostgreSqlConnectionString();

        Assert.Equal("Host=localhost;Username=openlineops;Password=openlineops;Database=openlineops", connectionString);
    }

    [Fact]
    public void ConstructorCreatesRepositoryWithoutOpeningDatabaseConnection()
    {
        using var repository = new PostgresProcessDefinitionRepository(
            "Host=localhost;Username=openlineops;Password=openlineops;Database=openlineops");

        Assert.NotNull(repository);
    }
}
