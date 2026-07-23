using Microsoft.Data.Sqlite;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Application.Monitoring;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class ProductionLineRuntimeStateReaderTests
{
    private static readonly DateTimeOffset BaseTimeUtc =
        new(2026, 7, 11, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RebuildsParallelStationsQueuesSlotsAndCarrierFromSqliteAfterRestart()
    {
        using var database = TemporarySqliteDatabase.Create();
        var unitInSlot = CreateUnit("SN-SLOT-001");
        var unitOnCarrier = CreateUnit("SN-CARRIER-001");
        var queuedUnit = CreateUnit("SN-QUEUED-001");
        var occupiedUnit = CreateUnit("SN-OCCUPIED-001");
        var carrier = Carrier.Register(
            new CarrierId("carrier-01"),
            "tray-12",
            12,
            "operator-a",
            BaseTimeUtc);
        var stationA = MaterialLocation.AtStation("line-main", "station-a");
        var stationB = MaterialLocation.AtStation("line-main", "station-b");
        var runningSlot = SlotOccupancy.Register(
            new SlotAddress("line-main", "station-a", "slot-running"),
            BaseTimeUtc);
        var slotMaterial = MaterialReference.ForProductionUnit(unitInSlot.Id);
        var availableSlot = SlotOccupancy.Register(
            new SlotAddress("line-main", "station-a", "slot-available"),
            BaseTimeUtc);
        var reservedSlot = SlotOccupancy.Register(
            new SlotAddress("line-main", "station-a", "slot-reserved"),
            BaseTimeUtc);
        var blockedSlot = SlotOccupancy.Register(
            new SlotAddress("line-main", "station-b", "slot-blocked"),
            BaseTimeUtc);
        var occupiedSlot = SlotOccupancy.Register(
            new SlotAddress("line-main", "station-b", "slot-occupied"),
            BaseTimeUtc);
        var occupiedMaterial = MaterialReference.ForProductionUnit(occupiedUnit.Id);
        var offlineSlot = SlotOccupancy.Register(
            new SlotAddress("line-main", "station-b", "slot-offline"),
            BaseTimeUtc);

        var runA = CreateRun(
            unitInSlot,
            "station-a",
            "operation-a",
            [
                new ResourceRequirement(ResourceKind.Station, "station-a"),
                new ResourceRequirement(ResourceKind.Slot, "line-main/station-a/slot-running"),
                new ResourceRequirement(ResourceKind.Fixture, "fixture-a"),
                new ResourceRequirement(ResourceKind.Device, "device-a")
            ]);
        var runB = CreateRun(
            unitOnCarrier,
            "station-b",
            "operation-b",
            [new ResourceRequirement(ResourceKind.Station, "station-b")],
            carrier.Id.Value);

        var leaseClock = new FixedClock(BaseTimeUtc);
        using (var runRepository = new SqliteProductionRunRepository(database.ConnectionString))
        using (var materialRepository = new SqliteProductionMaterialRepository(database.ConnectionString))
        using (var leaseRepository = new SqliteResourceLeaseRepository(
                   database.ConnectionString,
                   leaseClock))
        {
            Assert.True(await materialRepository.TryAddAsync(unitInSlot));
            Assert.True(await materialRepository.TryAddAsync(unitOnCarrier));
            Assert.True(await materialRepository.TryAddAsync(queuedUnit));
            Assert.True(await materialRepository.TryAddAsync(occupiedUnit));
            Assert.True(await materialRepository.TryAddAsync(carrier));
            Assert.True(await materialRepository.TryAddAsync(runningSlot));
            Assert.True(await materialRepository.TryAddAsync(availableSlot));
            Assert.True(await materialRepository.TryAddAsync(reservedSlot));
            Assert.True(await materialRepository.TryAddAsync(blockedSlot));
            Assert.True(await materialRepository.TryAddAsync(occupiedSlot));
            Assert.True(await materialRepository.TryAddAsync(offlineSlot));
            var materialService = new ProductionMaterialService(materialRepository, runRepository);
            foreach (var arrival in new[]
                     {
                         (MaterialReference.ForProductionUnit(unitInSlot.Id), stationA),
                         (MaterialReference.ForProductionUnit(unitOnCarrier.Id), stationB),
                         (MaterialReference.ForProductionUnit(queuedUnit.Id), stationA),
                         (MaterialReference.ForProductionUnit(occupiedUnit.Id), stationB),
                         (MaterialReference.ForCarrier(carrier.Id), stationB)
                     })
            {
                Assert.True((await materialService.ArriveAsync(new ArriveMaterialCommand(
                    Guid.NewGuid(),
                    arrival.Item1,
                    arrival.Item2,
                    "operator-a",
                    BaseTimeUtc.AddSeconds(1)))).Succeeded);
            }

            Assert.True((await materialService.TransferAsync(new TransferMaterialCommand(
                MaterialReference.ForProductionUnit(unitOnCarrier.Id),
                stationB,
                MaterialLocation.OnCarrier(carrier.Id, "position-01"),
                "operator-a",
                BaseTimeUtc.AddSeconds(2)))).Succeeded);
            Assert.True((await materialService.ReserveSlotAsync(new ReserveSlotCommand(
                runningSlot.Address,
                slotMaterial,
                "operator-a",
                BaseTimeUtc.AddSeconds(2)))).Succeeded);
            Assert.True((await materialService.LoadSlotAsync(new LoadSlotCommand(
                runningSlot.Address,
                slotMaterial,
                "operator-a",
                BaseTimeUtc.AddSeconds(3)))).Succeeded);
            Assert.True((await materialService.StartSlotAsync(new StartSlotCommand(
                runningSlot.Address,
                slotMaterial,
                "operator-a",
                BaseTimeUtc.AddSeconds(4)))).Succeeded);
            Assert.True((await materialService.ReserveSlotAsync(new ReserveSlotCommand(
                reservedSlot.Address,
                MaterialReference.ForProductionUnit(queuedUnit.Id),
                "operator-a",
                BaseTimeUtc.AddSeconds(2)))).Succeeded);
            Assert.True((await materialService.BlockSlotAsync(new BlockSlotCommand(
                blockedSlot.Address,
                "maintenance",
                "operator-a",
                BaseTimeUtc.AddSeconds(1)))).Succeeded);
            Assert.True((await materialService.ReserveSlotAsync(new ReserveSlotCommand(
                occupiedSlot.Address,
                occupiedMaterial,
                "operator-a",
                BaseTimeUtc.AddSeconds(2)))).Succeeded);
            Assert.True((await materialService.LoadSlotAsync(new LoadSlotCommand(
                occupiedSlot.Address,
                occupiedMaterial,
                "operator-a",
                BaseTimeUtc.AddSeconds(3)))).Succeeded);
            Assert.True((await materialService.SetSlotOfflineAsync(new SetSlotOfflineCommand(
                offlineSlot.Address,
                "operator-a",
                BaseTimeUtc.AddSeconds(1)))).Succeeded);
            await PersistRunningRunAsync(runRepository, materialRepository, leaseRepository, runA);
            await PersistRunningRunAsync(runRepository, materialRepository, leaseRepository, runB);
        }

        using var restartedRuns = new SqliteProductionRunRepository(database.ConnectionString);
        using var restartedMaterials = new SqliteProductionMaterialRepository(database.ConnectionString);
        using var restartedLeases = new SqliteResourceLeaseRepository(
            database.ConnectionString,
            new FixedClock(BaseTimeUtc.AddMinutes(1)));
        var presenceRepository = new InMemoryAgentPresenceRepository();
        Assert.True(await presenceRepository.RecordAsync(new AgentPresenceReported(
            "agent-a",
            "station-a-host",
            "station-a",
            Guid.NewGuid(),
            1,
            AgentPresenceState.Started,
            BaseTimeUtc.AddSeconds(55)),
            BaseTimeUtc.AddSeconds(55)));
        Assert.True(await presenceRepository.RecordAsync(new AgentPresenceReported(
            "agent-b",
            "station-b-host",
            "station-b",
            Guid.NewGuid(),
            1,
            AgentPresenceState.Started,
            BaseTimeUtc.AddSeconds(55)),
            BaseTimeUtc.AddSeconds(55)));
        var reader = new ProductionLineRuntimeStateReader(
            restartedRuns,
            restartedMaterials,
            restartedLeases,
            presenceRepository,
            new AgentPresenceMonitoringOptions(),
            new FixedClock(BaseTimeUtc.AddMinutes(1)));

        var state = await reader.ReadAsync("line-main");

        Assert.Equal(2, state.ActiveRuns.Count);
        Assert.Equal(4, state.ProductionUnits.Count);
        var stationStates = state.Stations.ToDictionary(static station => station.StationSystemId);
        Assert.Equal(2, stationStates.Count);
        Assert.All(stationStates.Values, station =>
            Assert.Equal(ProductionLineStationRuntimeStatus.Running, station.Status));
        Assert.Contains(
            stationStates["station-a"].Queue,
            item => item.MaterialKind == MaterialKind.ProductionUnit
                && item.MaterialId == queuedUnit.Id.ToString());
        Assert.Contains(
            stationStates["station-b"].Queue,
            item => item.MaterialKind == MaterialKind.Carrier
                && item.MaterialId == carrier.Id.Value);

        var stationAOperation = Assert.Single(stationStates["station-a"].ActiveOperations);
        Assert.Equal(runA.Run.Id, stationAOperation.ProductionRunId);
        Assert.Equal(unitInSlot.Id, stationAOperation.ProductionUnitId);
        Assert.Equal(4, stationAOperation.Resources.Count);
        Assert.All(stationAOperation.Resources, resource =>
        {
            Assert.Equal(ProductionLineResourceRuntimeStatus.Leased, resource.Status);
            Assert.True(resource.FencingToken > 0);
            Assert.NotNull(resource.AcquiredAtUtc);
            Assert.NotNull(resource.ExpiresAtUtc);
        });

        Assert.Equal(
            [
                SlotOccupancyStatus.Available,
                SlotOccupancyStatus.Reserved,
                SlotOccupancyStatus.Running,
                SlotOccupancyStatus.Blocked,
                SlotOccupancyStatus.Occupied,
                SlotOccupancyStatus.Offline
            ],
            state.Slots.Select(static slot => slot.Status).ToArray());
        var restoredCarrier = Assert.Single(state.Carriers);
        Assert.Equal(carrier.Id.Value, restoredCarrier.CarrierId);
        Assert.Equal(stationB, restoredCarrier.Location);
        var position = Assert.Single(restoredCarrier.ProductionUnits);
        Assert.Equal("position-01", position.CarrierPositionId);
        Assert.Equal(unitOnCarrier.Id, position.ProductionUnitId);
        Assert.Equal(ResultJudgement.Unknown, position.Judgement);
    }

    [Fact]
    public async Task PendingOperationReportsWaitingResourcesWithoutInventingFencingTokens()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var runs = new InMemoryProductionRunRepository(materials);
        var leases = new InMemoryResourceLeaseRepository(new FixedClock(BaseTimeUtc));
        var unit = CreateUnit("SN-WAITING-001");
        Assert.True(await materials.TryAddAsync(unit));
        var run = CreateRun(
            unit,
            "station-waiting",
            "operation-waiting",
            [new ResourceRequirement(ResourceKind.Station, "station-waiting")]);
        var unitEntry = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
            await materials.GetProductionUnitAsync(unit.Id));
        Assert.True(await runs.TryAddAsync(
            run.Run,
            run.Plan,
            new ProductionRunAdmission(unitEntry.Aggregate.ToSnapshot(), unitEntry.Revision)));
        Assert.True(run.Run.Start(BaseTimeUtc).Succeeded);
        Assert.Equal(1, await runs.SaveAsync(run.Run, 0));
        var presenceRepository = new InMemoryAgentPresenceRepository();
        Assert.True(await presenceRepository.RecordAsync(new AgentPresenceReported(
            "agent-waiting",
            "station-waiting-host",
            "station-waiting",
            Guid.NewGuid(),
            1,
            AgentPresenceState.Started,
            BaseTimeUtc),
            BaseTimeUtc));
        var reader = new ProductionLineRuntimeStateReader(
            runs,
            materials,
            leases,
            presenceRepository,
            new AgentPresenceMonitoringOptions(),
            new FixedClock(BaseTimeUtc.AddSeconds(1)));

        var state = await reader.ReadAsync("line-main");

        var station = Assert.Single(state.Stations);
        Assert.Equal(ProductionLineStationRuntimeStatus.WaitingForResources, station.Status);
        Assert.Equal(ProductionLineAgentPresenceHealth.NotApplicable, station.AgentPresenceHealth);
        var resource = Assert.Single(Assert.Single(station.ActiveOperations).Resources);
        Assert.Equal(ProductionLineResourceRuntimeStatus.Waiting, resource.Status);
        Assert.Null(resource.FencingToken);
    }

    [Fact]
    public async Task RequiredPresenceUsesCoordinatorReceiveTtlAndReconnectRestoresOnline()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var runs = new InMemoryProductionRunRepository(materials);
        var leases = new InMemoryResourceLeaseRepository(new FixedClock(BaseTimeUtc));
        Assert.True(await materials.TryAddAsync(SlotOccupancy.Register(
            new SlotAddress("line-main", "station-presence", "slot-main"),
            BaseTimeUtc)));
        Assert.True(await materials.TryAddAsync(SlotOccupancy.Register(
            new SlotAddress("line-main", "station-missing", "slot-main"),
            BaseTimeUtc)));
        var presenceRepository = new InMemoryAgentPresenceRepository();
        var sessionId = Guid.NewGuid();
        Assert.True(await presenceRepository.RecordAsync(new AgentPresenceReported(
            "agent-presence",
            "station-host-presence",
            "station-presence",
            sessionId,
            1,
            AgentPresenceState.Started,
            BaseTimeUtc.AddDays(1)),
            BaseTimeUtc));
        var required = new AgentPresenceMonitoringOptions
        {
            TimeToLive = TimeSpan.FromSeconds(15),
            PresenceRequired = true
        };

        var expiredState = await new ProductionLineRuntimeStateReader(
            runs,
            materials,
            leases,
            presenceRepository,
            required,
            new FixedClock(BaseTimeUtc.AddSeconds(16))).ReadAsync("line-main");
        var expired = expiredState.Stations.Single(station =>
            station.StationSystemId == "station-presence");
        Assert.Equal(ProductionLineStationRuntimeStatus.Offline, expired.Status);
        Assert.Equal(ProductionLineAgentPresenceHealth.Expired, expired.AgentPresenceHealth);
        Assert.Equal(TimeSpan.FromSeconds(16), expired.AgentPresenceAge);
        Assert.Equal(BaseTimeUtc, expired.AgentPresenceLastSeenAtUtc);
        var missing = expiredState.Stations.Single(station =>
            station.StationSystemId == "station-missing");
        Assert.Equal(ProductionLineStationRuntimeStatus.Offline, missing.Status);
        Assert.Equal(ProductionLineAgentPresenceHealth.Missing, missing.AgentPresenceHealth);

        Assert.True(await presenceRepository.RecordAsync(new AgentPresenceReported(
            "agent-presence",
            "station-host-presence",
            "station-presence",
            sessionId,
            2,
            AgentPresenceState.Heartbeat,
            BaseTimeUtc.AddDays(-1)),
            BaseTimeUtc.AddSeconds(17)));
        var onlineState = await new ProductionLineRuntimeStateReader(
            runs,
            materials,
            leases,
            presenceRepository,
            required,
            new FixedClock(BaseTimeUtc.AddSeconds(17))).ReadAsync("line-main");
        var online = onlineState.Stations.Single(station =>
            station.StationSystemId == "station-presence");
        Assert.Equal(ProductionLineStationRuntimeStatus.Idle, online.Status);
        Assert.Equal(ProductionLineAgentPresenceHealth.Online, online.AgentPresenceHealth);
        Assert.Equal(TimeSpan.Zero, online.AgentPresenceAge);

        Assert.True(await presenceRepository.RecordAsync(new AgentPresenceReported(
            "agent-presence",
            "station-host-presence",
            "station-presence",
            sessionId,
            3,
            AgentPresenceState.Stopping,
            BaseTimeUtc.AddDays(-1)),
            BaseTimeUtc.AddSeconds(18)));
        var stoppingState = await new ProductionLineRuntimeStateReader(
            runs,
            materials,
            leases,
            presenceRepository,
            required,
            new FixedClock(BaseTimeUtc.AddSeconds(18))).ReadAsync("line-main");
        var stopping = stoppingState.Stations.Single(station =>
            station.StationSystemId == "station-presence");
        Assert.Equal(ProductionLineStationRuntimeStatus.Offline, stopping.Status);
        Assert.Equal(ProductionLineAgentPresenceHealth.Stopping, stopping.AgentPresenceHealth);
    }

    private static ProductionUnit CreateUnit(string identityValue) => ProductionUnit.Register(
        ProductionUnitId.New(),
        "board-main",
        "serialNumber",
        identityValue,
        null,
        "operator-a",
        BaseTimeUtc);

    private static RunFixture CreateRun(
        ProductionUnit unit,
        string stationSystemId,
        string operationId,
        IReadOnlyCollection<ResourceRequirement> resources,
        string? carrierId = null)
    {
        var runId = ProductionRunId.New();
        var process = new ExecutableRuntimeProcess(
            new ProcessDefinitionId($"process.{operationId}"),
            new ProcessVersionId($"process-version.{operationId}"),
            []);
        var operation = new OperationExecutionPlan(
            operationId,
            stationSystemId,
            new StationId(stationSystemId),
            new ConfigurationSnapshotId($"configuration.{operationId}"),
            new RecipeSnapshotId($"recipe.{operationId}"),
            process,
            [],
            resources);
        var run = ProductionRun.Create(
            runId,
            "project-main",
            "application-main",
            "snapshot-main",
            "topology-main",
            "line-main",
            unit.Id,
            new ProductionUnitIdentity(
                unit.ProductModelId,
                unit.IdentityKey,
                unit.IdentityValue),
            null,
            carrierId,
            "operator-a",
            operationId,
            BaseTimeUtc,
            [operation.Definition],
            []);
        return new RunFixture(run, new ProductionRunExecutionPlan(runId, [operation]));
    }

    private static async Task PersistRunningRunAsync(
        SqliteProductionRunRepository runRepository,
        SqliteProductionMaterialRepository materialRepository,
        SqliteResourceLeaseRepository leaseRepository,
        RunFixture fixture)
    {
        var unit = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
            await materialRepository.GetProductionUnitAsync(fixture.Run.ProductionUnitId));
        Assert.True(await runRepository.TryAddAsync(
            fixture.Run,
            fixture.Plan,
            new ProductionRunAdmission(unit.Aggregate.ToSnapshot(), unit.Revision)));
        Assert.True(fixture.Run.Start(BaseTimeUtc).Succeeded);
        var operation = Assert.Single(fixture.Run.Operations);
        var acquired = Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await leaseRepository.TryAcquireAsync(
                fixture.Run.Id,
                operation.OperationRunId,
                operation.ResourceRequirements,
                TimeSpan.FromMinutes(10)));
        Assert.True(fixture.Run.StartOperation(
            operation.OperationRunId,
            RuntimeSessionId.New(),
            acquired,
            BaseTimeUtc).Succeeded);
        Assert.Equal(1, await runRepository.SaveAsync(fixture.Run, 0));
    }

    private sealed record RunFixture(
        ProductionRun Run,
        ProductionRunExecutionPlan Plan);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class TemporarySqliteDatabase : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-line-state-{Guid.NewGuid():N}.sqlite");

        private TemporarySqliteDatabase()
        {
        }

        public string ConnectionString => new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        public static TemporarySqliteDatabase Create() => new();

        public void Dispose()
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
    }
}
