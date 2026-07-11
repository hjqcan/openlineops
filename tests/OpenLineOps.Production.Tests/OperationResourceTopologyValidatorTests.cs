using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Production.Application.LineDefinitions;
using OpenLineOps.Production.Domain.Identifiers;
using OpenLineOps.Production.Domain.Models;
using OpenLineOps.Topology.Application.Topologies;

namespace OpenLineOps.Production.Tests;

public sealed class OperationResourceTopologyValidatorTests
{
    [Fact]
    public void ExactLineControllerAuthorizationAllowsOnlyItsDeclaredRemoteDriverAction()
    {
        var topology = Topology();
        var operation = Operation("binding.axis-a");
        var authorization = new LineControllerAuthorization(
            new LineControllerAuthorizationId("authorization.remote-axis"),
            operation.Id,
            "operation.move:action:1",
            "system.axis-a",
            "binding.axis-a",
            "motion.axis",
            "Move",
            "station.b",
            "system.axis-b",
            "binding.axis-b",
            "motion.axis",
            "Move");

        Assert.Null(OperationResourceTopologyValidator.ValidateLineControllerAuthorization(
            operation,
            authorization,
            topology));
        Assert.True(OperationResourceTopologyValidator.IsLineControllerActionAuthorized(
            Action("operation.move:action:1", "motion.axis", "Move", "binding.axis-b"),
            authorization));
        Assert.False(OperationResourceTopologyValidator.IsLineControllerActionAuthorized(
            Action("operation.move:action:1", "motion.axis", "Move", "binding.axis-a"),
            authorization));
        Assert.False(OperationResourceTopologyValidator.IsLineControllerActionAuthorized(
            Action("operation.move:action:1", "motion.axis", "Home", "binding.axis-b"),
            authorization));
    }

    [Fact]
    public void LineControllerAuthorizationRejectsControllerOutsideOperationStation()
    {
        var topology = Topology();
        var operation = Operation("binding.axis-a");
        var authorization = new LineControllerAuthorization(
            new LineControllerAuthorizationId("authorization.invalid-controller"),
            operation.Id,
            "action.remote-axis",
            "system.axis-b",
            "binding.axis-b",
            "motion.axis",
            "Move",
            "station.a",
            "system.axis-a",
            "binding.axis-a",
            "motion.axis",
            "Move");

        var failure = OperationResourceTopologyValidator.ValidateLineControllerAuthorization(
            operation,
            authorization,
            topology);

        Assert.NotNull(failure);
        Assert.Equal("LineControllerProviderInvalid", failure.Code);
    }

    [Fact]
    public void RecursiveStationOwnershipAuthorizesOnlyTheOwningStationSubtree()
    {
        var topology = Topology();
        var ownedOperation = Operation("binding.axis-a");

        Assert.Null(OperationResourceTopologyValidator.Validate(ownedOperation, topology));
        Assert.True(OperationResourceTopologyValidator.IsTargetWithinStation(
            "System",
            "system.axis-a",
            ownedOperation,
            topology));
        Assert.True(OperationResourceTopologyValidator.IsTargetWithinStation(
            "Driver",
            "binding.axis-a",
            ownedOperation,
            topology));
        Assert.True(OperationResourceTopologyValidator.IsTargetAuthorized(
            "Capability",
            "motion.axis",
            ownedOperation,
            topology));
        Assert.False(OperationResourceTopologyValidator.IsTargetWithinStation(
            "System",
            "system.axis-b",
            ownedOperation,
            topology));
        Assert.False(OperationResourceTopologyValidator.IsTargetWithinStation(
            "Driver",
            "binding.axis-b",
            ownedOperation,
            topology));

        var crossStation = Operation("binding.axis-b");
        var failure = OperationResourceTopologyValidator.Validate(crossStation, topology);
        Assert.NotNull(failure);
        Assert.Equal("OperationDeviceResourceInvalid", failure.Code);
    }

    private static OperationDefinition Operation(string deviceTargetId) =>
        OperationDefinition.Create(
            new OperationDefinitionId("operation.move"),
            "Move",
            "station.a",
            "flow.move",
            "configuration.move",
            [
                new OperationResourceBinding(
                    new OperationResourceBindingId("resource.station"),
                    OperationResourceKind.Station,
                    "station.a",
                    OperationResourceResolution.Fixed),
                new OperationResourceBinding(
                    new OperationResourceBindingId("resource.axis"),
                    OperationResourceKind.Device,
                    deviceTargetId,
                    OperationResourceResolution.Fixed)
            ]);

    private static AutomationTopologyDetails Topology() => new(
        "topology.main",
        "Main",
        new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero),
        [
            System("station.a", null, "Station"),
            System("system.cell-a", "station.a", "System"),
            System("system.axis-a", "system.cell-a", "System", ["motion.axis"]),
            System("station.b", null, "Station"),
            System("system.axis-b", "station.b", "System", ["motion.axis"])
        ],
        [new CapabilityContractDetails(
            "motion.axis",
            "Move",
            "1.0",
            null,
            null,
            30,
            "Normal")],
        [
            new DriverBindingDetails(
                "binding.axis-a",
                "system.axis-a",
                "motion.axis",
                "DeviceInstance",
                "axis-a"),
            new DriverBindingDetails(
                "binding.axis-b",
                "system.axis-b",
                "motion.axis",
                "DeviceInstance",
                "axis-b")
        ],
        [],
        []);

    private static AutomationSystemDetails System(
        string id,
        string? parentId,
        string kind,
        IReadOnlyCollection<string>? capabilities = null) => new(
        id,
        parentId,
        kind,
        kind,
        id,
        [],
        capabilities ?? [],
        new Dictionary<string, string>());

    private static FlowIrAction Action(
        string actionId,
        string capabilityId,
        string commandName,
        string bindingId) => new(
        actionId,
        FlowIrActionKind.DeviceCommand,
        actionId,
        capabilityId,
        commandName,
        new FlowIrTargetReference(FlowIrTargetReferenceKind.Driver, bindingId),
        null,
        new FlowIrExecutionPolicy(1_000, 0, FlowIrCancellationMode.Cooperative),
        null,
        new FlowIrSourceTrace(
            "flow.main",
            "flow.main@1",
            FlowIrSourceElementKind.ProcessNode,
            actionId,
            null));
}
