using OpenLineOps.Runtime.Application.Stations;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class StationProductionExecutionGateTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 31, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task UnenrolledStationPreservesCompatibilityExecution()
    {
        var gate = new StationProductionExecutionGate(
            new InMemoryStationLifecycleRepository());

        var result = await gate.EvaluateAsync("station-unmanaged");

        Assert.False(result.Managed);
        Assert.True(result.Allowed);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public async Task ManagedStoppedStationBlocksProductionDispatch()
    {
        var repository = new InMemoryStationLifecycleRepository();
        var station = StationLifecycle.Create(
            new StationId("station-managed"),
            StationMode.Automatic,
            StationReadiness.ReadyToExecute,
            "agent-a",
            Now);
        Assert.True(await repository.TryAddAsync(station));
        var gate = new StationProductionExecutionGate(repository);

        var result = await gate.EvaluateAsync("station-managed");

        Assert.True(result.Managed);
        Assert.False(result.Allowed);
        Assert.Contains("not Execute", result.Reason, StringComparison.Ordinal);
        Assert.NotNull(result.Evidence);
    }

    [Fact]
    public async Task ManagedExecuteStationRequiresAllReadinessAndProducesRevisionEvidence()
    {
        var repository = new InMemoryStationLifecycleRepository();
        var station = StationLifecycle.Create(
            new StationId("station-execute"),
            StationMode.Automatic,
            StationReadiness.ReadyToExecute,
            "agent-a",
            Now);
        var authorization = new StationCommandAuthorization(
            "agent-a",
            StationCommandGrant.All);
        Assert.True(station.Reset(authorization, "Prepare station.", Now).Succeeded);
        Assert.True(
            station.AcknowledgeTransition(
                authorization,
                "Reset completed.",
                Now.AddMilliseconds(1)).Succeeded);
        Assert.True(
            station.Start(
                authorization,
                "Start automatic execution.",
                Now.AddMilliseconds(2)).Succeeded);
        Assert.True(
            station.AcknowledgeTransition(
                authorization,
                "Start completed.",
                Now.AddMilliseconds(3)).Succeeded);
        Assert.True(await repository.TryAddAsync(station));
        var gate = new StationProductionExecutionGate(repository);

        var result = await gate.EvaluateAsync("station-execute");

        Assert.True(result.Managed);
        Assert.True(result.Allowed);
        Assert.Contains(
            "station-lifecycle:station-execute:0:Automatic:Execute",
            result.Evidence,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManagedSimulationStationCanRunButMaintenanceStationCannot()
    {
        var simulation = await CreateExecutingStationAsync(
            "station-simulation",
            StationMode.Simulation);
        var maintenance = await CreateExecutingStationAsync(
            "station-maintenance",
            StationMode.Maintenance);

        Assert.True((await simulation.EvaluateAsync("station-simulation")).Allowed);
        Assert.False((await maintenance.EvaluateAsync("station-maintenance")).Allowed);
    }

    private static async Task<StationProductionExecutionGate> CreateExecutingStationAsync(
        string stationId,
        StationMode mode)
    {
        var repository = new InMemoryStationLifecycleRepository();
        var station = StationLifecycle.Create(
            new StationId(stationId),
            mode,
            StationReadiness.ReadyToExecute,
            "agent-a",
            Now);
        var authorization = new StationCommandAuthorization(
            "agent-a",
            StationCommandGrant.All);
        station.Reset(authorization, "Prepare station.", Now);
        station.AcknowledgeTransition(
            authorization,
            "Reset completed.",
            Now.AddMilliseconds(1));
        station.Start(
            authorization,
            "Start execution.",
            Now.AddMilliseconds(2));
        station.AcknowledgeTransition(
            authorization,
            "Start completed.",
            Now.AddMilliseconds(3));
        Assert.True(await repository.TryAddAsync(station));
        return new StationProductionExecutionGate(repository);
    }
}
