using OpenLineOps.Topology.Domain.Capabilities;
using OpenLineOps.Topology.Domain.DriverBindings;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Layouts;
using OpenLineOps.Topology.Domain.Slots;
using OpenLineOps.Topology.Domain.Systems;
using OpenLineOps.Topology.Domain.Topology;

namespace OpenLineOps.Topology.Tests;

public sealed class AutomationTopologyTests
{
    [Fact]
    public void StationIsARealAutomationSystemSubtypeAndOwnsSlots()
    {
        var topology = CreateTopology();
        var capability = CapabilityContract.Create(
            new CapabilityContractId("test.execute"),
            "Run",
            new Version(1, 0),
            null,
            null,
            TimeSpan.FromSeconds(5),
            SafetyClass.Normal);
        Assert.True(topology.AddCapability(capability).Succeeded);

        var station = AutomationSystem.Create(
            new AutomationSystemId("station.eol"),
            null,
            SystemKind.Station,
            "vendor.eol-tester",
            "EOL Station",
            providedCapabilities: [capability.Id],
            metadata: new Dictionary<string, string> { ["vendor"] = "acme" });
        Assert.IsType<StationSystem>(station);
        Assert.True(topology.AddSystem(station).Succeeded);

        var group = SlotGroup.Create(
            new SlotGroupId("group.fixture"),
            station.Id,
            "Fixture",
            SlotGroupKind.TesterBank,
            1);
        Assert.True(topology.AddSlotGroup(group).Succeeded);
        Assert.True(topology.AddSlot(SlotDefinition.Create(
            new SlotDefinitionId("slot.1"),
            group.Id,
            station.Id,
            "1",
            "Production Unit 1")).Succeeded);

        Assert.Single(topology.Systems);
        Assert.Single(topology.SlotGroups);
        Assert.Single(topology.Slots);
        Assert.Equal("acme", topology.Systems.Single().Metadata["vendor"]);
    }

    [Fact]
    public void SystemReferencesMustResolveStrictly()
    {
        var topology = CreateTopology();
        var missingParent = topology.AddSystem(AutomationSystem.Create(
            new AutomationSystemId("child"),
            new AutomationSystemId("missing"),
            SystemKind.System,
            "axis",
            "Axis"));
        Assert.False(missingParent.Succeeded);
        Assert.Equal("Topology.ParentSystemMissing", missingParent.Code);

        var missingCapability = topology.AddSystem(AutomationSystem.Create(
            new AutomationSystemId("station"),
            null,
            SystemKind.Station,
            "tester",
            "Tester",
            providedCapabilities: [new CapabilityContractId("missing")]));
        Assert.False(missingCapability.Succeeded);
        Assert.Equal("Topology.SystemCapabilityMissing", missingCapability.Code);
    }

    [Fact]
    public void StationMustBeRootWhileGeneralSystemsMayFormRecursiveSubtrees()
    {
        var topology = AutomationTopology.Create(
            new AutomationTopologyId("topology.hierarchy"),
            "Hierarchy",
            DateTimeOffset.UtcNow);
        var station = AutomationSystem.Create(
            new AutomationSystemId("station.root"),
            null,
            SystemKind.Station,
            "station",
            "Station");
        Assert.True(topology.AddSystem(station).Succeeded);

        var nestedStation = topology.AddSystem(AutomationSystem.Create(
            new AutomationSystemId("station.nested"),
            station.Id,
            SystemKind.Station,
            "station",
            "Nested Station"));
        var orphanGeneral = topology.AddSystem(AutomationSystem.Create(
            new AutomationSystemId("system.orphan"),
            null,
            SystemKind.System,
            "component",
            "Orphan General System"));
        var generalUnderStation = topology.AddSystem(AutomationSystem.Create(
            new AutomationSystemId("system.station-child"),
            station.Id,
            SystemKind.System,
            "component",
            "Station Component"));
        var generalUnderGeneral = topology.AddSystem(AutomationSystem.Create(
            new AutomationSystemId("system.nested"),
            new AutomationSystemId("system.station-child"),
            SystemKind.System,
            "component",
            "Nested General System"));

        Assert.False(nestedStation.Succeeded);
        Assert.Equal("Topology.StationMustBeRoot", nestedStation.Code);
        Assert.False(orphanGeneral.Succeeded);
        Assert.Equal("Topology.ChildSystemRequiresStationTree", orphanGeneral.Code);
        Assert.True(generalUnderStation.Succeeded);
        Assert.True(generalUnderGeneral.Succeeded);
    }

    [Fact]
    public void RecursiveStationSubtreeAllowsSameCapabilityOnDifferentOwnerSystemsOnly()
    {
        var topology = CreateTopology();
        var capability = CapabilityContract.Create(
            new CapabilityContractId("motion.axis"),
            "Move",
            new Version(1, 0),
            null,
            null,
            TimeSpan.FromSeconds(5));
        Assert.True(topology.AddCapability(capability).Succeeded);
        var station = AutomationSystem.Create(
            new AutomationSystemId("station.main"),
            null,
            SystemKind.Station,
            "station",
            "Main Station");
        var cell = AutomationSystem.Create(
            new AutomationSystemId("system.cell"),
            station.Id,
            SystemKind.System,
            "cell",
            "Cell");
        var axisA = AutomationSystem.Create(
            new AutomationSystemId("system.axis-a"),
            cell.Id,
            SystemKind.System,
            "axis",
            "Axis A",
            providedCapabilities: [capability.Id]);
        var axisB = AutomationSystem.Create(
            new AutomationSystemId("system.axis-b"),
            cell.Id,
            SystemKind.System,
            "axis",
            "Axis B",
            providedCapabilities: [capability.Id]);
        Assert.True(topology.AddSystem(station).Succeeded);
        Assert.True(topology.AddSystem(cell).Succeeded);
        Assert.True(topology.AddSystem(axisA).Succeeded);
        Assert.True(topology.AddSystem(axisB).Succeeded);

        Assert.True(topology.AddDriverBinding(DriverBinding.Create(
            new DriverBindingId("binding.axis-a"),
            axisA.Id,
            capability.Id,
            DriverProviderKind.PluginCommand,
            "axis-a")).Succeeded);
        Assert.True(topology.AddDriverBinding(DriverBinding.Create(
            new DriverBindingId("binding.axis-b"),
            axisB.Id,
            capability.Id,
            DriverProviderKind.PluginCommand,
            "axis-b")).Succeeded);
        var duplicateOwnerCapability = topology.AddDriverBinding(DriverBinding.Create(
            new DriverBindingId("binding.axis-a-duplicate"),
            axisA.Id,
            capability.Id,
            DriverProviderKind.Simulator,
            "axis-a-simulator"));

        Assert.False(duplicateOwnerCapability.Succeeded);
        Assert.Equal("Topology.CapabilityAlreadyBound", duplicateOwnerCapability.Code);
        Assert.Equal(2, topology.DriverBindings.Count);
    }

    [Fact]
    public void SlotGroupRejectsNonStationParentAndSlotRejectsMismatchedSystem()
    {
        var topology = CreateTopology();
        var station = AutomationSystem.Create(
            new AutomationSystemId("station.main"),
            null,
            SystemKind.Station,
            "station",
            "Station");
        Assert.True(topology.AddSystem(station).Succeeded);
        var generic = AutomationSystem.Create(
            new AutomationSystemId("system.generic"),
            station.Id,
            SystemKind.System,
            "generic",
            "Generic");
        Assert.True(topology.AddSystem(generic).Succeeded);

        var rejected = topology.AddSlotGroup(SlotGroup.Create(
            new SlotGroupId("group.invalid"),
            generic.Id,
            "Invalid",
            SlotGroupKind.FixtureNest,
            1));
        Assert.False(rejected.Succeeded);
        Assert.Equal("Topology.SlotGroupRequiresStation", rejected.Code);

        var group = SlotGroup.Create(
            new SlotGroupId("group.main"),
            station.Id,
            "Main",
            SlotGroupKind.FixtureNest,
            1);
        Assert.True(topology.AddSlotGroup(group).Succeeded);
        var mismatched = topology.AddSlot(SlotDefinition.Create(
            new SlotDefinitionId("slot.invalid"),
            group.Id,
            generic.Id,
            "1",
            "Invalid"));
        Assert.False(mismatched.Succeeded);
        Assert.Equal("Topology.SlotSystemMismatch", mismatched.Code);
    }

    [Fact]
    public void LayoutUsesParentLocalCoordinatesAndRejectsEscapingChildren()
    {
        var layout = SiteLayout.Create(
            new SiteLayoutId("layout.main"),
            new AutomationTopologyId("topology.main"),
            "Main",
            1000,
            600);
        var station = SiteLayoutElement.Create(
            new LayoutElementId("element.station"),
            LayoutElementKind.SystemShape,
            new LayoutTargetReference(LayoutTargetKind.System, "station.main"),
            null,
            100,
            100,
            400,
            300,
            0,
            1);
        Assert.True(layout.AddElement(station).Succeeded);

        var group = SiteLayoutElement.Create(
            new LayoutElementId("element.group"),
            LayoutElementKind.GroupRegion,
            new LayoutTargetReference(LayoutTargetKind.SlotGroup, "group.main"),
            station.Id,
            20,
            30,
            200,
            120,
            0,
            2);
        Assert.True(layout.AddElement(group).Succeeded);

        var escapingSlot = SiteLayoutElement.Create(
            new LayoutElementId("element.slot"),
            LayoutElementKind.SlotShape,
            new LayoutTargetReference(LayoutTargetKind.Slot, "slot.1"),
            group.Id,
            190,
            20,
            20,
            20,
            0,
            3);
        var result = layout.AddElement(escapingSlot);
        Assert.False(result.Succeeded);
        Assert.Equal("Topology.LayoutElementOutOfBounds", result.Code);
    }

    [Fact]
    public void SystemUpdatePreservesIdentityKindAndParentWhileSystemDeleteCascadesTheSubtree()
    {
        var topology = CreateTopology();
        var capability = CapabilityContract.Create(
            new CapabilityContractId("component.inspect"),
            "Inspect",
            new Version(1, 0),
            null,
            null,
            TimeSpan.FromSeconds(5));
        Assert.True(topology.AddCapability(capability).Succeeded);
        var station = AutomationSystem.Create(
            new AutomationSystemId("Station.Main"),
            null,
            SystemKind.Station,
            "station.old",
            "Old Station");
        var child = AutomationSystem.Create(
            new AutomationSystemId("System.Child"),
            station.Id,
            SystemKind.System,
            "component.old",
            "Old Component");
        Assert.True(topology.AddSystem(station).Succeeded);
        Assert.True(topology.AddSystem(child).Succeeded);
        var group = SlotGroup.Create(
            new SlotGroupId("Group.Main"),
            station.Id,
            "Fixture",
            SlotGroupKind.FixtureNest,
            2);
        Assert.True(topology.AddSlotGroup(group).Succeeded);
        Assert.True(topology.AddSlot(SlotDefinition.Create(
            new SlotDefinitionId("Slot.Main"),
            group.Id,
            station.Id,
            "A1",
            "Production Unit")).Succeeded);

        Assert.True(topology.UpdateSystem(
            child.Id,
            "component.new",
            "Renamed Component",
            [capability.Id],
            [capability.Id],
            new Dictionary<string, string> { ["vendor"] = "acme" }).Succeeded);
        var updated = topology.FindSystem(child.Id)!;
        Assert.Equal(child.Id, updated.Id);
        Assert.Equal(station.Id, updated.ParentSystemId);
        Assert.Equal(SystemKind.System, updated.Kind);
        Assert.Equal("component.new", updated.SystemType);
        Assert.Equal("Renamed Component", updated.DisplayName);
        Assert.Equal([capability.Id], updated.RequiredCapabilities);
        Assert.Equal([capability.Id], updated.ProvidedCapabilities);

        Assert.True(topology.AddDriverBinding(DriverBinding.Create(
            new DriverBindingId("binding.component.inspect"),
            child.Id,
            capability.Id,
            DriverProviderKind.Simulator,
            "component-inspector")).Succeeded);
        var removeBoundCapability = topology.UpdateSystem(
            child.Id,
            updated.SystemType,
            updated.DisplayName,
            [],
            [],
            updated.Metadata);
        Assert.False(removeBoundCapability.Succeeded);
        Assert.Equal("Topology.SystemCapabilityInUse", removeBoundCapability.Code);

        Assert.True(topology.RemoveSystem(station.Id).Succeeded);
        Assert.Empty(topology.Systems);
        Assert.Empty(topology.SlotGroups);
        Assert.Empty(topology.Slots);
        Assert.Equal(
            "Topology.SystemNotFound",
            topology.RemoveSystem(new AutomationSystemId("station.main")).Code);
    }

    [Fact]
    public void SlotGroupCapacityAndStationScopedSlotAddressesRemainStrict()
    {
        var topology = CreateTopology();
        var station = AutomationSystem.Create(
            new AutomationSystemId("station.main"),
            null,
            SystemKind.Station,
            "station",
            "Station");
        Assert.True(topology.AddSystem(station).Succeeded);
        var firstGroup = SlotGroup.Create(
            new SlotGroupId("group.first"),
            station.Id,
            "First",
            SlotGroupKind.FixtureNest,
            2);
        var secondGroup = SlotGroup.Create(
            new SlotGroupId("group.second"),
            station.Id,
            "Second",
            SlotGroupKind.TesterBank,
            2);
        Assert.True(topology.AddSlotGroup(firstGroup).Succeeded);
        Assert.True(topology.AddSlotGroup(secondGroup).Succeeded);
        Assert.True(topology.AddSlot(SlotDefinition.Create(
            new SlotDefinitionId("slot.first"), firstGroup.Id, station.Id, "A1", "First")).Succeeded);
        Assert.True(topology.AddSlot(SlotDefinition.Create(
            new SlotDefinitionId("slot.first.2"), firstGroup.Id, station.Id, "B1", "First 2")).Succeeded);
        Assert.True(topology.AddSlot(SlotDefinition.Create(
            new SlotDefinitionId("slot.second"), secondGroup.Id, station.Id, "a1", "Second")).Succeeded);

        var duplicate = topology.UpdateSlot(
            new SlotDefinitionId("slot.second"),
            "A1",
            "Second",
            SlotMaterialKind.Carrier,
            false);
        Assert.False(duplicate.Succeeded);
        Assert.Equal("Topology.SlotAddressAlreadyExists", duplicate.Code);
        var tooSmall = topology.UpdateSlotGroup(
            firstGroup.Id,
            "First",
            SlotGroupKind.BufferLane,
            1);
        Assert.False(tooSmall.Succeeded);
        Assert.Equal("Topology.SlotGroupCapacityBelowSlotCount", tooSmall.Code);

        Assert.True(topology.UpdateSlotGroup(
            firstGroup.Id,
            "Renamed Group",
            SlotGroupKind.BufferLane,
            2).Succeeded);
        Assert.Equal("Renamed Group", topology.FindSlotGroup(firstGroup.Id)!.DisplayName);
        Assert.True(topology.RemoveSlotGroup(firstGroup.Id).Succeeded);
        Assert.Null(topology.FindSlot(new SlotDefinitionId("slot.first")));
        Assert.NotNull(topology.FindSlot(new SlotDefinitionId("slot.second")));
    }

    [Fact]
    public void LayoutPresentationUpdatesAndTargetRemovalDeleteTheWholeElementSubtree()
    {
        var layout = SiteLayout.Create(
            new SiteLayoutId("layout.main"),
            new AutomationTopologyId("topology.main"),
            "Main",
            1000,
            600);
        var station = SiteLayoutElement.Create(
            new LayoutElementId("station.shape"),
            LayoutElementKind.SystemShape,
            new LayoutTargetReference(LayoutTargetKind.System, "station.main"),
            null,
            20,
            20,
            400,
            300,
            0,
            1);
        var group = SiteLayoutElement.Create(
            new LayoutElementId("group.region"),
            LayoutElementKind.GroupRegion,
            new LayoutTargetReference(LayoutTargetKind.SlotGroup, "group.main"),
            station.Id,
            20,
            80,
            180,
            120,
            0,
            2);
        var slot = SiteLayoutElement.Create(
            new LayoutElementId("slot.shape"),
            LayoutElementKind.SlotShape,
            new LayoutTargetReference(LayoutTargetKind.Slot, "slot.main"),
            group.Id,
            10,
            40,
            28,
            26,
            0,
            3);
        Assert.True(layout.AddElement(station).Succeeded);
        Assert.True(layout.AddElement(group).Succeeded);
        Assert.True(layout.AddElement(slot).Succeeded);

        Assert.True(layout.UpdateElementPresentation(
            group.Id,
            12,
            new Dictionary<string, string> { ["appearance"] = "fixture" }).Succeeded);
        Assert.Equal(12, layout.Elements.Single(element => element.Id == group.Id).ZIndex);
        Assert.Equal("fixture", layout.Elements.Single(element => element.Id == group.Id).Style["appearance"]);

        var removed = layout.RemoveElementsByTargets(new HashSet<LayoutTargetReference>
        {
            new(LayoutTargetKind.SlotGroup, "group.main")
        });
        Assert.Equal(2, removed);
        Assert.Single(layout.Elements);
        Assert.Equal(station.Id, layout.Elements.Single().Id);
    }

    private static AutomationTopology CreateTopology() => AutomationTopology.Create(
        new AutomationTopologyId("topology.main"),
        "Main",
        DateTimeOffset.UtcNow);
}
