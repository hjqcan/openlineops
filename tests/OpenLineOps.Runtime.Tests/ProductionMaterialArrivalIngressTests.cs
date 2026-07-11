using Microsoft.Data.Sqlite;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class ProductionMaterialArrivalIngressTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "openlineops-material-arrival-inbox",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SqliteInboxColdRestartReturnsPersistedResultWithoutDuplicatingTimeline()
    {
        Directory.CreateDirectory(_directory);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "runtime.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        var unitId = ProductionUnitId.New();
        var message = new MaterialArrived(
            Guid.NewGuid(),
            "material-arrival/plc/unit-main/scan-001",
            "agent.main",
            "station.main",
            unitId.Value,
            "line.main",
            "station-system.main",
            StationMaterialArrivalSources.Plc,
            "plc.reader.main",
            Now);

        using (var runs = new SqliteProductionRunRepository(connectionString))
        using (var materials = new SqliteProductionMaterialRepository(connectionString))
        using (var inbox = new SqliteProductionMaterialArrivalInbox(connectionString))
        {
            Assert.True(await materials.TryAddAsync(ProductionUnit.Register(
                unitId,
                "product.board",
                "serialNumber",
                "SN-ARRIVAL-001",
                null,
                "operator.main",
                Now.AddMinutes(-1))));
            var ingress = new ProductionMaterialArrivalIngress(
                inbox,
                materials,
                new ProductionMaterialService(materials, runs),
                new FixedClock(Now.AddSeconds(1)));
            var first = await ingress.HandleAsync(message);
            Assert.True(first.Succeeded, first.Message);
        }

        using (var runs = new SqliteProductionRunRepository(connectionString))
        using (var materials = new SqliteProductionMaterialRepository(connectionString))
        using (var inbox = new SqliteProductionMaterialArrivalInbox(connectionString))
        {
            var ingress = new ProductionMaterialArrivalIngress(
                inbox,
                materials,
                new ProductionMaterialService(materials, runs),
                new FixedClock(Now.AddSeconds(2)));
            var duplicate = await ingress.HandleAsync(message);
            Assert.True(duplicate.Succeeded, duplicate.Message);
            var timeline = await materials.ListTimelineAsync(
                new ProductionMaterialTimelineQuery(productionUnitId: unitId));
            Assert.Single(timeline, static evidence =>
                evidence.Kind == ProductionMaterialEvidenceKind.LocationTransition);
        }
    }

    [Fact]
    public async Task InMemoryInboxRejectsMessageIdentityReusedWithDifferentEvidence()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var runs = new InMemoryProductionRunRepository(materials);
        var unitId = ProductionUnitId.New();
        Assert.True(await materials.TryAddAsync(ProductionUnit.Register(
            unitId,
            "product.board",
            "serialNumber",
            "SN-ARRIVAL-002",
            null,
            "operator.main",
            Now.AddMinutes(-1))));
        var ingress = new ProductionMaterialArrivalIngress(
            new InMemoryProductionMaterialArrivalInbox(),
            materials,
            new ProductionMaterialService(materials, runs),
            new FixedClock(Now.AddSeconds(1)));
        var message = new MaterialArrived(
            Guid.NewGuid(),
            "material-arrival/manual/unit-main/scan-002",
            "operator-terminal.main",
            "station.main",
            unitId.Value,
            "line.main",
            "station-system.main",
            StationMaterialArrivalSources.Manual,
            "operator.main",
            Now);
        Assert.True((await ingress.HandleAsync(message)).Succeeded);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ingress.HandleAsync(message with { LineId = "line.other" }));
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private sealed class FixedClock(DateTimeOffset nowUtc) : IClock
    {
        public DateTimeOffset UtcNow => nowUtc;
    }
}
