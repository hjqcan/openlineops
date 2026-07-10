using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Topology.Domain.Capabilities;
using OpenLineOps.Topology.Domain.DriverBindings;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Layouts;
using OpenLineOps.Topology.Domain.Modules;
using OpenLineOps.Topology.Domain.Nodes;
using OpenLineOps.Topology.Domain.Slots;
using OpenLineOps.Topology.Domain.Topology;
using OpenLineOps.Topology.Infrastructure.Persistence;

namespace OpenLineOps.Topology.Tests;

public sealed class FileSystemProjectTopologyRepositoryTests : IDisposable
{
    private readonly string _projectDirectory = Path.Combine(
        Path.GetTempPath(),
        "openlineops-project-topology-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProjectTopologyAndLayoutSurviveNewRepositoryInstancesAndIsolateApplications()
    {
        var applicationA = new ProjectApplicationWorkspaceScope(
            "project.shared",
            "application.a",
            _projectDirectory);
        var applicationB = new ProjectApplicationWorkspaceScope(
            "project.shared",
            "application.b",
            _projectDirectory);
        var topologyId = new AutomationTopologyId("topology.main");
        var layoutId = new SiteLayoutId("layout.main");

        var topologyA = CreateCompleteTopology(topologyId, "Application A Topology");
        var layoutA = CreateCompleteLayout(layoutId, topologyId, x: 120);
        var topologyB = AutomationTopology.Create(
            topologyId,
            "Application B Topology",
            new DateTimeOffset(2026, 7, 10, 2, 0, 0, TimeSpan.Zero));
        Assert.True(topologyB.AddEquipmentNode(EquipmentNode.Create(
            new EquipmentNodeId("site.main"),
            null,
            EquipmentNodeKind.Site,
            "Application B Site")).Succeeded);
        var layoutB = SiteLayout.Create(layoutId, topologyId, "Application B Layout", 800, 600, "mm");
        Assert.True(layoutB.AddElement(SiteLayoutElement.Create(
            new LayoutElementId("element.site"),
            LayoutElementKind.NodeShape,
            new LayoutTargetReference(LayoutTargetKind.EquipmentNode, "site.main"),
            420,
            30,
            100,
            80,
            0,
            "equipment",
            "Application B Site")).Succeeded);

        var writerTopologyRepository = new FileSystemProjectAutomationTopologyRepository();
        var writerLayoutRepository = new FileSystemProjectSiteLayoutRepository();
        await writerTopologyRepository.SaveAsync(applicationA, topologyA);
        await writerLayoutRepository.SaveAsync(applicationA, layoutA);
        await writerTopologyRepository.SaveAsync(applicationB, topologyB);
        await writerLayoutRepository.SaveAsync(applicationB, layoutB);

        var readerTopologyRepository = new FileSystemProjectAutomationTopologyRepository();
        var readerLayoutRepository = new FileSystemProjectSiteLayoutRepository();
        var restoredTopologyA = await readerTopologyRepository.GetByIdAsync(applicationA, topologyId);
        var restoredLayoutA = await readerLayoutRepository.GetByIdAsync(applicationA, layoutId);
        var restoredTopologyB = await readerTopologyRepository.GetByIdAsync(applicationB, topologyId);
        var restoredLayoutB = await readerLayoutRepository.GetByIdAsync(applicationB, layoutId);

        Assert.NotNull(restoredTopologyA);
        Assert.Equal("Application A Topology", restoredTopologyA.DisplayName);
        Assert.Equal(3, restoredTopologyA.Nodes.Count);
        Assert.Single(restoredTopologyA.Capabilities);
        Assert.Single(restoredTopologyA.Modules);
        Assert.Single(restoredTopologyA.DriverBindings);
        Assert.Single(restoredTopologyA.SlotGroups);
        Assert.Single(restoredTopologyA.Slots);
        Assert.Equal("L1", restoredTopologyA.Slots.Single().Address);
        Assert.Equal("simulator.axis.x", restoredTopologyA.DriverBindings.Single().ProviderKey);

        Assert.NotNull(restoredLayoutA);
        Assert.Equal(2, restoredLayoutA.Elements.Count);
        Assert.Equal(120, restoredLayoutA.Elements.Single(element => element.Id.Value == "element.station").X);
        Assert.Equal(15, restoredLayoutA.Elements.Single(element => element.Id.Value == "element.slot").RotationDegrees);

        Assert.NotNull(restoredTopologyB);
        Assert.Equal("Application B Topology", restoredTopologyB.DisplayName);
        Assert.Single(restoredTopologyB.Nodes);
        Assert.Empty(restoredTopologyB.Capabilities);
        Assert.NotNull(restoredLayoutB);
        Assert.Equal(420, restoredLayoutB.Elements.Single().X);

        Assert.Equal(2, Directory.GetFiles(_projectDirectory, "topology-*.json", SearchOption.AllDirectories).Length);
        Assert.Equal(2, Directory.GetFiles(_projectDirectory, "layout-*.json", SearchOption.AllDirectories).Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectDirectory))
        {
            Directory.Delete(_projectDirectory, recursive: true);
        }
    }

    private static AutomationTopology CreateCompleteTopology(
        AutomationTopologyId topologyId,
        string displayName)
    {
        var topology = AutomationTopology.Create(
            topologyId,
            displayName,
            new DateTimeOffset(2026, 7, 10, 1, 0, 0, TimeSpan.Zero));

        Assert.True(topology.AddEquipmentNode(EquipmentNode.Create(
            new EquipmentNodeId("site.main"),
            null,
            EquipmentNodeKind.Site,
            "Main Site")).Succeeded);
        Assert.True(topology.AddEquipmentNode(EquipmentNode.Create(
            new EquipmentNodeId("line.main"),
            new EquipmentNodeId("site.main"),
            EquipmentNodeKind.Line,
            "Main Line")).Succeeded);
        Assert.True(topology.AddEquipmentNode(EquipmentNode.Create(
            new EquipmentNodeId("station.left"),
            new EquipmentNodeId("line.main"),
            EquipmentNodeKind.Station,
            "Left Station")).Succeeded);
        Assert.True(topology.AddCapability(CapabilityContract.Create(
            new CapabilityContractId("motion.axis.move"),
            "MoveAxis",
            new Version(1, 2, 0),
            "{\"type\":\"object\"}",
            "{\"completed\":\"boolean\"}",
            TimeSpan.FromSeconds(12),
            SafetyClass.Motion)).Succeeded);
        Assert.True(topology.AddModule(AutomationModule.Create(
            new AutomationModuleId("module.axis.x"),
            new EquipmentNodeId("station.left"),
            "AxisMotion",
            "X Axis",
            [new CapabilityContractId("motion.axis.move")],
            [new CapabilityContractId("motion.axis.move")])).Succeeded);
        Assert.True(topology.AddDriverBinding(DriverBinding.Create(
            new DriverBindingId("binding.axis.x"),
            new CapabilityContractId("motion.axis.move"),
            DriverProviderKind.Simulator,
            "simulator.axis.x")).Succeeded);
        Assert.True(topology.AddSlotGroup(SlotGroup.Create(
            new SlotGroupId("slot-group.left"),
            new EquipmentNodeId("station.left"),
            "Left Nest",
            SlotGroupKind.FixtureNest,
            2)).Succeeded);
        Assert.True(topology.AddSlotToGroup(
            new SlotGroupId("slot-group.left"),
            SlotDefinition.Create(
                new SlotDefinitionId("slot.left.1"),
                new EquipmentNodeId("station.left"),
                "L1",
                "Left Slot 1",
                SlotMaterialKind.Dut,
                true)).Succeeded);

        return topology;
    }

    private static SiteLayout CreateCompleteLayout(
        SiteLayoutId layoutId,
        AutomationTopologyId topologyId,
        double x)
    {
        var layout = SiteLayout.Create(layoutId, topologyId, "Main Layout", 1200, 800, "mm");
        Assert.True(layout.AddElement(SiteLayoutElement.Create(
            new LayoutElementId("element.station"),
            LayoutElementKind.NodeShape,
            new LayoutTargetReference(LayoutTargetKind.EquipmentNode, "station.left"),
            x,
            100,
            500,
            300,
            0,
            "equipment",
            "Left Station")).Succeeded);
        Assert.True(layout.AddElement(SiteLayoutElement.Create(
            new LayoutElementId("element.slot"),
            LayoutElementKind.SlotShape,
            new LayoutTargetReference(LayoutTargetKind.Slot, "slot.left.1"),
            180,
            180,
            80,
            60,
            15,
            "material",
            "L1")).Succeeded);

        return layout;
    }
}
