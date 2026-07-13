using OpenLineOps.Projects.Application.Releases;

namespace OpenLineOps.Projects.Tests;

public sealed class ProjectReleaseStationDeploymentSetTests
{
    [Fact]
    public void ResolveUsesOperationAndExactLineControllerTargetStations()
    {
        var line = new ProjectReleaseProductionLine(
            "line.main",
            "Main Line",
            "topology.main",
            new ProjectReleaseProductModel("product.main", "MAIN", "serialNumber"),
            "operation.first",
            [
                Operation("operation.first", "station.z"),
                Operation("operation.second", "station.a"),
                Operation("operation.third", "station.z")
            ],
            [
                Transition("transition.first-second", "operation.first", "operation.second"),
                Transition("transition.second-third", "operation.second", "operation.third"),
                Transition("transition.third-completed", "operation.third", null)
            ],
            [
                Authorization("authorization.remote", "station.b"),
                Authorization("authorization.local-target", "station.a")
            ]);

        var stationSystemIds = ProjectReleaseStationDeploymentSet.Resolve(line);

        Assert.Equal(["station.a", "station.b", "station.z"], stationSystemIds);
    }

    private static ProjectReleaseOperation Operation(string operationId, string stationSystemId) => new(
        operationId,
        operationId,
        stationSystemId,
        $"flow.{operationId}",
        $"configuration.{operationId}",
        $"flow.{operationId}@1.0.0",
        "openlineops.flow-ir",
        new string('a', 64),
        "{}",
        [],
        [],
        []);

    private static ProjectReleaseRouteTransition Transition(
        string transitionId,
        string sourceOperationId,
        string? targetOperationId) => new(
        transitionId,
        sourceOperationId,
        targetOperationId,
        targetOperationId is null ? "Completed" : null,
        "Sequence",
        null,
        null,
        null,
        null,
        null,
        null);

    private static ProjectReleaseLineControllerAuthorization Authorization(
        string authorizationId,
        string targetStationSystemId) => new(
        authorizationId,
        "operation.first",
        $"action.{authorizationId}",
        "system.controller",
        "binding.controller",
        "capability.controller",
        "Control",
        targetStationSystemId,
        $"{targetStationSystemId}.system.target",
        $"{targetStationSystemId}.binding.target",
        "capability.target",
        "Execute");
}
