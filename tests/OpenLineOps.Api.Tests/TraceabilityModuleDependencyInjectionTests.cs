using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenLineOps.Traceability.Api.DependencyInjection;
using OpenLineOps.Traceability.Api.RuntimeIntegration;
using OpenLineOps.Traceability.Infrastructure.Persistence;

namespace OpenLineOps.Api.Tests;

public sealed class TraceabilityModuleDependencyInjectionTests
{
    [Fact]
    public void ProjectionRebuildIsDisabledByDefaultWithBoundedPageSize()
    {
        var services = new ServiceCollection();
        services.AddOpenLineOpsTraceabilityModule(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<TraceProjectionRebuildOptions>();

        Assert.False(options.Enabled);
        Assert.Equal(TraceProjectionRebuildOptions.DefaultPageSize, options.PageSize);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    [InlineData("1.5")]
    [InlineData(" 10")]
    public void ProjectionRebuildRejectsInvalidPageSize(string pageSize)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Traceability:ProjectionRebuild:PageSize"] = pageSize
            })
            .Build();

        Assert.ThrowsAny<Exception>(() =>
            new ServiceCollection().AddOpenLineOpsTraceabilityModule(configuration));
    }

    [Fact]
    public async Task DisabledProjectionRebuildDoesNotReadProductionEvidence()
    {
        var rebuilder = new RecordingProjectionRebuilder();
        var service = new TraceProjectionRebuildHostedService(
            rebuilder,
            new TraceProjectionRebuildOptions(enabled: false, pageSize: 10));

        await service.StartAsync(default);

        Assert.Equal(0, rebuilder.CallCount);
    }

    [Fact]
    public async Task EnabledProjectionRebuildBlocksStartupAndPropagatesFailure()
    {
        var failure = new InvalidDataException("Frozen terminal evidence cannot be projected.");
        var rebuilder = new RecordingProjectionRebuilder(failure);
        var service = new TraceProjectionRebuildHostedService(
            rebuilder,
            new TraceProjectionRebuildOptions(enabled: true, pageSize: 37));

        var observed = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.StartAsync(default));

        Assert.Same(failure, observed);
        Assert.Equal(1, rebuilder.CallCount);
        Assert.Equal(37, rebuilder.PageSize);
    }

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

    private sealed class RecordingProjectionRebuilder(Exception? failure = null)
        : ITraceProjectionRebuilder
    {
        public int CallCount { get; private set; }

        public int? PageSize { get; private set; }

        public ValueTask<TraceProjectionRebuildResult> RebuildAsync(
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            PageSize = pageSize;
            return failure is null
                ? ValueTask.FromResult(new TraceProjectionRebuildResult(0, 0, 0, 1))
                : ValueTask.FromException<TraceProjectionRebuildResult>(failure);
        }
    }
}
