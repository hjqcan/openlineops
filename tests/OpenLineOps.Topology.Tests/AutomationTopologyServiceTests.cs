using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Topology.Application.Layouts;
using OpenLineOps.Topology.Application.Topologies;
using OpenLineOps.Topology.Infrastructure.Persistence;

namespace OpenLineOps.Topology.Tests;

public sealed class AutomationTopologyServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 2, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task ServiceComposesTopologyAndAddsLayoutElementsForExistingTargets()
    {
        var service = CreateService();

        var created = await service.CreateAsync(new CreateAutomationTopologyRequest("topology.main", "Main Line"));
        var site = await service.AddEquipmentNodeAsync(
            "topology.main",
            new AddEquipmentNodeRequest("node.site", null, "Site", "Factory Site"));
        var line = await service.AddEquipmentNodeAsync(
            "topology.main",
            new AddEquipmentNodeRequest("node.line.1", "node.site", "Line", "Line 1"));
        var station = await service.AddEquipmentNodeAsync(
            "topology.main",
            new AddEquipmentNodeRequest("node.station.1", "node.line.1", "Station", "Station 1"));
        var capability = await service.AddCapabilityAsync(
            "topology.main",
            new AddCapabilityContractRequest(
                "motion.axis.move",
                "motion.axis.move",
                "1.0.0",
                "{}",
                "{}",
                5,
                "Motion"));
        var module = await service.AddModuleAsync(
            "topology.main",
            new AddAutomationModuleRequest(
                "module.axis.x",
                "node.station.1",
                "AxisMotion",
                "X Axis",
                ["motion.axis.move"],
                ["motion.axis.move"]));
        var binding = await service.AddDriverBindingAsync(
            "topology.main",
            new AddDriverBindingRequest(
                "binding.axis.x.simulator",
                "motion.axis.move",
                "Simulator",
                "simulator.axis.x"));
        var slotGroup = await service.AddSlotGroupAsync(
            "topology.main",
            new AddSlotGroupRequest(
                "slot-group.left-nest",
                "node.station.1",
                "Left Nest",
                "FixtureNest",
                2));
        var slot = await service.AddSlotAsync(
            "topology.main",
            new AddSlotDefinitionRequest(
                "slot-group.left-nest",
                "slot.left-nest.1",
                "node.station.1",
                "L1",
                "Left Nest Slot 1",
                "Dut"));
        var layout = await service.CreateLayoutAsync(new CreateSiteLayoutRequest(
            "layout.main",
            "topology.main",
            "Main Layout",
            1920,
            1080,
            "px"));
        var withStationElement = await service.AddLayoutElementAsync(
            "layout.main",
            new AddSiteLayoutElementRequest(
                "element.station.1",
                "NodeShape",
                "EquipmentNode",
                "node.station.1",
                100,
                200,
                320,
                180,
                0,
                "default",
                "Station 1"));
        var withSlotElement = await service.AddLayoutElementAsync(
            "layout.main",
            new AddSiteLayoutElementRequest(
                "element.slot.1",
                "SlotShape",
                "Slot",
                "slot.left-nest.1",
                140,
                240,
                40,
                40,
                0,
                "default",
                "L1"));
        var movedSlotElement = await service.UpdateLayoutElementGeometryAsync(
            "layout.main",
            "element.slot.1",
            new UpdateSiteLayoutElementGeometryRequest(
                180,
                260,
                44,
                42,
                15));

        Assert.True(created.IsSuccess);
        Assert.True(site.IsSuccess);
        Assert.True(line.IsSuccess);
        Assert.True(station.IsSuccess);
        Assert.True(capability.IsSuccess);
        Assert.True(module.IsSuccess);
        Assert.True(binding.IsSuccess);
        Assert.True(slotGroup.IsSuccess);
        Assert.True(slot.IsSuccess);
        Assert.True(layout.IsSuccess);
        Assert.True(withStationElement.IsSuccess);
        Assert.True(withSlotElement.IsSuccess);
        Assert.True(movedSlotElement.IsSuccess);
        Assert.Equal(2, movedSlotElement.Value.Elements.Count);
        var movedElement = movedSlotElement.Value.Elements.Single(element => element.ElementId == "element.slot.1");
        Assert.Equal(180, movedElement.X);
        Assert.Equal(260, movedElement.Y);
        Assert.Equal(44, movedElement.Width);
        Assert.Equal(42, movedElement.Height);
        Assert.Equal(15, movedElement.RotationDegrees);
    }

    [Fact]
    public async Task AddLayoutElementRejectsMissingTopologyTarget()
    {
        var service = CreateService();

        Assert.True((await service.CreateAsync(new CreateAutomationTopologyRequest("topology.main", "Main Line"))).IsSuccess);
        Assert.True((await service.AddEquipmentNodeAsync(
            "topology.main",
            new AddEquipmentNodeRequest("node.site", null, "Site", "Factory Site"))).IsSuccess);
        Assert.True((await service.CreateLayoutAsync(new CreateSiteLayoutRequest(
            "layout.main",
            "topology.main",
            "Main Layout",
            1920,
            1080,
            "px"))).IsSuccess);

        var result = await service.AddLayoutElementAsync(
            "layout.main",
            new AddSiteLayoutElementRequest(
                "element.missing",
                "NodeShape",
                "EquipmentNode",
                "node.missing",
                0,
                0,
                100,
                100,
                0,
                "default",
                "Missing"));

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Topology.LayoutTargetMissing", result.Error.Code);
    }

    [Fact]
    public async Task UpdateLayoutElementGeometryRejectsOutOfBoundsElement()
    {
        var service = CreateService();

        Assert.True((await service.CreateAsync(new CreateAutomationTopologyRequest("topology.main", "Main Line"))).IsSuccess);
        Assert.True((await service.AddEquipmentNodeAsync(
            "topology.main",
            new AddEquipmentNodeRequest("node.site", null, "Site", "Factory Site"))).IsSuccess);
        Assert.True((await service.CreateLayoutAsync(new CreateSiteLayoutRequest(
            "layout.main",
            "topology.main",
            "Main Layout",
            200,
            160,
            "px"))).IsSuccess);
        Assert.True((await service.AddLayoutElementAsync(
            "layout.main",
            new AddSiteLayoutElementRequest(
                "element.site",
                "NodeShape",
                "EquipmentNode",
                "node.site",
                20,
                30,
                80,
                60,
                0,
                "default",
                "Site"))).IsSuccess);

        var result = await service.UpdateLayoutElementGeometryAsync(
            "layout.main",
            "element.site",
            new UpdateSiteLayoutElementGeometryRequest(
                180,
                130,
                80,
                60,
                0));

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Topology.LayoutElementOutOfBounds", result.Error.Code);
    }

    private static AutomationTopologyService CreateService()
    {
        return new AutomationTopologyService(
            new InMemoryAutomationTopologyRepository(),
            new InMemorySiteLayoutRepository(),
            new FixedClock(Now));
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }
}
