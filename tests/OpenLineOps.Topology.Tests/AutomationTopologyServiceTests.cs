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
        Assert.Equal(2, withSlotElement.Value.Elements.Count);
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
