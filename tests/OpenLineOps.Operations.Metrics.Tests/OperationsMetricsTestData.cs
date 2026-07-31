using Microsoft.Data.Sqlite;
using OpenLineOps.Operations.Metrics.Application.Services;
using OpenLineOps.Operations.Metrics.Domain.Production;
using OpenLineOps.Operations.Metrics.Infrastructure.Persistence;

namespace OpenLineOps.Operations.Metrics.Tests;

internal static class OperationsMetricsTestData
{
    public static readonly DateTimeOffset BaseUtc =
        new(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);

    public static CanonicalProductionEvent Completed(
        string eventId,
        DateTimeOffset occurredAtUtc,
        bool firstAttempt = true,
        bool good = true,
        double cycleSeconds = 50,
        DateTimeOffset? receivedAtUtc = null) =>
        new(
            eventId,
            "station-a",
            $"unit-{eventId}",
            ProductionEventKind.UnitCompleted,
            occurredAtUtc.AddMilliseconds(-50),
            occurredAtUtc,
            receivedAtUtc ?? occurredAtUtc.AddSeconds(1),
            firstAttempt,
            good,
            TimeSpan.FromSeconds(cycleSeconds),
            CanonicalProductionEvent.CurrentSchemaVersion);

    public static StoreFixture CreateStoreFixture()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "openlineops-operations-metrics-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "metrics.sqlite");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();
        return new StoreFixture(directory, connectionString);
    }
}

internal sealed class StoreFixture(
    string directory,
    string connectionString) : IDisposable
{
    private readonly List<IDisposable> _stores = [];

    public string ConnectionString { get; } = connectionString;

    public SqliteOperationsMetricsStore CreateStore()
    {
        var store = new SqliteOperationsMetricsStore(ConnectionString);
        _stores.Add(store);
        return store;
    }

    public OperationsMetricsService CreateService(
        DateTimeOffset utcNow,
        SqliteOperationsMetricsStore? store = null)
    {
        return new OperationsMetricsService(
            store ?? CreateStore(),
            new FixedTimeProvider(utcNow));
    }

    public void Dispose()
    {
        foreach (var store in _stores.AsEnumerable().Reverse())
        {
            store.Dispose();
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
