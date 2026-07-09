using OpenLineOps.Topology.Domain.Capabilities;
using OpenLineOps.Topology.Domain.DriverBindings;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Modules;
using OpenLineOps.Topology.Domain.Nodes;
using OpenLineOps.Topology.Domain.Slots;
using OpenLineOps.Topology.Domain.Topology;

namespace OpenLineOps.Topology.Tests;

public sealed class AutomationTopologyTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 7, 9, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TopologyComposesEquipmentCapabilitiesBindingsSlotsAndLayoutTargets()
    {
        var topology = AutomationTopology.Create(new AutomationTopologyId("topology.main"), "Main Line", CreatedAtUtc);
        var site = EquipmentNode.Create(new EquipmentNodeId("node.site"), null, EquipmentNodeKind.Site, "Factory Site");
        var line = EquipmentNode.Create(new EquipmentNodeId("node.line.1"), site.Id, EquipmentNodeKind.Line, "Line 1");
        var station = EquipmentNode.Create(new EquipmentNodeId("node.station.1"), line.Id, EquipmentNodeKind.Station, "Station 1");
        var capabilityId = new CapabilityContractId("motion.axis.move");
        var capability = CapabilityContract.Create(
            capabilityId,
            "motion.axis.move",
            new Version(1, 0, 0),
            "{}",
            "{}",
            TimeSpan.FromSeconds(5),
            SafetyClass.Motion);
        var module = AutomationModule.Create(
            new AutomationModuleId("module.axis.x"),
            station.Id,
            "AxisMotion",
            "X Axis",
            [capabilityId],
            [capabilityId]);
        var binding = DriverBinding.Create(
            new DriverBindingId("binding.axis.x.simulator"),
            capabilityId,
            DriverProviderKind.Simulator,
            "simulator.axis.x");
        var slotGroup = SlotGroup.Create(
            new SlotGroupId("slot-group.left-nest"),
            station.Id,
            "Left Nest",
            SlotGroupKind.FixtureNest,
            capacity: 2);
        var slot = SlotDefinition.Create(
            new SlotDefinitionId("slot.left-nest.1"),
            station.Id,
            "L1",
            "Left Nest Slot 1");

        Assert.True(topology.AddEquipmentNode(site).Succeeded);
        Assert.True(topology.AddEquipmentNode(line).Succeeded);
        Assert.True(topology.AddEquipmentNode(station).Succeeded);
        Assert.True(topology.AddCapability(capability).Succeeded);
        Assert.True(topology.AddModule(module).Succeeded);
        Assert.True(topology.AddDriverBinding(binding).Succeeded);
        Assert.True(topology.AddSlotGroup(slotGroup).Succeeded);
        Assert.True(topology.AddSlotToGroup(slotGroup.Id, slot).Succeeded);

        Assert.Equal(3, topology.Nodes.Count);
        Assert.Single(topology.Modules);
        Assert.Single(topology.DriverBindings);
        Assert.Single(topology.Slots);
        Assert.True(topology.HasLayoutTarget(station.Id.Value));
        Assert.True(topology.HasLayoutTarget(module.Id.Value));
        Assert.True(topology.HasLayoutTarget(slotGroup.Id.Value));
        Assert.True(topology.HasLayoutTarget(slot.Id.Value));
    }

    [Fact]
    public void AddEquipmentNodeRejectsInvalidChildKind()
    {
        var topology = AutomationTopology.Create(new AutomationTopologyId("topology.main"), "Main Line", CreatedAtUtc);
        var site = EquipmentNode.Create(new EquipmentNodeId("node.site"), null, EquipmentNodeKind.Site, "Factory Site");
        var nestedSite = EquipmentNode.Create(new EquipmentNodeId("node.site.nested"), site.Id, EquipmentNodeKind.Site, "Nested Site");

        Assert.True(topology.AddEquipmentNode(site).Succeeded);
        var result = topology.AddEquipmentNode(nestedSite);

        Assert.False(result.Succeeded);
        Assert.Equal("Topology.NodeKindNotAllowed", result.Code);
    }

    [Fact]
    public void AddDriverBindingRequiresDeclaredCapability()
    {
        var topology = AutomationTopology.Create(new AutomationTopologyId("topology.main"), "Main Line", CreatedAtUtc);
        var binding = DriverBinding.Create(
            new DriverBindingId("binding.axis.x.simulator"),
            new CapabilityContractId("motion.axis.move"),
            DriverProviderKind.Simulator,
            "simulator.axis.x");

        var result = topology.AddDriverBinding(binding);

        Assert.False(result.Succeeded);
        Assert.Equal("Topology.DriverBindingCapabilityMissing", result.Code);
    }

    [Fact]
    public void AddSlotToGroupEnforcesCapacity()
    {
        var topology = CreateTopologyWithStation(out var stationId);
        var slotGroup = SlotGroup.Create(
            new SlotGroupId("slot-group.left-nest"),
            stationId,
            "Left Nest",
            SlotGroupKind.FixtureNest,
            capacity: 1);
        var firstSlot = SlotDefinition.Create(
            new SlotDefinitionId("slot.left-nest.1"),
            stationId,
            "L1",
            "Left Nest Slot 1");
        var secondSlot = SlotDefinition.Create(
            new SlotDefinitionId("slot.left-nest.2"),
            stationId,
            "L2",
            "Left Nest Slot 2");

        Assert.True(topology.AddSlotGroup(slotGroup).Succeeded);
        Assert.True(topology.AddSlotToGroup(slotGroup.Id, firstSlot).Succeeded);
        var result = topology.AddSlotToGroup(slotGroup.Id, secondSlot);

        Assert.False(result.Succeeded);
        Assert.Equal("Topology.SlotGroupCapacityExceeded", result.Code);
    }

    private static AutomationTopology CreateTopologyWithStation(out EquipmentNodeId stationId)
    {
        var topology = AutomationTopology.Create(new AutomationTopologyId("topology.main"), "Main Line", CreatedAtUtc);
        var site = EquipmentNode.Create(new EquipmentNodeId("node.site"), null, EquipmentNodeKind.Site, "Factory Site");
        var line = EquipmentNode.Create(new EquipmentNodeId("node.line.1"), site.Id, EquipmentNodeKind.Line, "Line 1");
        var station = EquipmentNode.Create(new EquipmentNodeId("node.station.1"), line.Id, EquipmentNodeKind.Station, "Station 1");

        Assert.True(topology.AddEquipmentNode(site).Succeeded);
        Assert.True(topology.AddEquipmentNode(line).Succeeded);
        Assert.True(topology.AddEquipmentNode(station).Succeeded);

        stationId = station.Id;

        return topology;
    }
}
