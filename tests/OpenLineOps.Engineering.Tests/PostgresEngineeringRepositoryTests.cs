using OpenLineOps.Engineering.Infrastructure.Persistence;

namespace OpenLineOps.Engineering.Tests;

public sealed class PostgresEngineeringRepositoryTests
{
    [Fact]
    public void ResolvePostgreSqlConnectionStringRequiresExplicitConnectionString()
    {
        var options = new EngineeringPersistenceOptions
        {
            Provider = EngineeringPersistenceProviders.PostgreSql,
            ConnectionString = " "
        };

        var exception = Assert.Throws<InvalidOperationException>(options.ResolvePostgreSqlConnectionString);

        Assert.Equal("PostgreSQL engineering persistence requires ConnectionString.", exception.Message);
    }

    [Fact]
    public void ResolvePostgreSqlConnectionStringTrimsConfiguredConnectionString()
    {
        var options = new EngineeringPersistenceOptions
        {
            Provider = EngineeringPersistenceProviders.PostgreSql,
            ConnectionString = " Host=localhost;Username=openlineops;Password=openlineops;Database=openlineops "
        };

        var connectionString = options.ResolvePostgreSqlConnectionString();

        Assert.Equal("Host=localhost;Username=openlineops;Password=openlineops;Database=openlineops", connectionString);
    }

    [Fact]
    public void ConstructorsCreateRepositoriesWithoutOpeningDatabaseConnection()
    {
        const string connectionString = "Host=localhost;Username=openlineops;Password=openlineops;Database=openlineops";

        using var workspaceRepository = new PostgresWorkspaceRepository(connectionString);
        using var projectRepository = new PostgresEngineeringProjectRepository(connectionString);
        using var recipeRepository = new PostgresRecipeRepository(connectionString);
        using var stationProfileRepository = new PostgresStationProfileRepository(connectionString);

        Assert.NotNull(workspaceRepository);
        Assert.NotNull(projectRepository);
        Assert.NotNull(recipeRepository);
        Assert.NotNull(stationProfileRepository);
    }
}
