using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Traceability.Api.DependencyInjection;
using OpenLineOps.Traceability.Infrastructure.Persistence;

namespace OpenLineOps.Api.Tests;

public sealed class TraceabilityModuleDependencyInjectionTests
{
    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=file:trace?mode=memory&cache=shared")]
    [InlineData("Data Source=trace;Mode=Memory;Cache=Shared")]
    public void SqliteTraceabilityRejectsTransientConnectionStrings(string connectionString)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new SqliteTraceRecordRepository(connectionString));

        Assert.Contains("file-backed database", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("Memory")]
    [InlineData("Postgres")]
    [InlineData("PostgreSql")]
    [InlineData("PostgreSQL")]
    [InlineData("postgresql")]
    public void AddOpenLineOpsTraceabilityModuleRejectsNonCanonicalPersistenceProviderTokens(
        string provider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Traceability:Persistence:Provider"] = provider
            })
            .Build();
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddOpenLineOpsTraceabilityModule(configuration));

        Assert.Contains(
            "Expected exactly 'Sqlite' or 'InMemory'",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LocalFile")]
    [InlineData("File")]
    [InlineData("filesystem")]
    public void AddOpenLineOpsTraceabilityModuleRejectsNonCanonicalArtifactStorageProviderTokens(
        string provider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Traceability:ArtifactStorage:Provider"] = provider
            })
            .Build();
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddOpenLineOpsTraceabilityModule(configuration));

        Assert.Contains("Expected exactly 'FileSystem'", exception.Message, StringComparison.Ordinal);
    }
}
