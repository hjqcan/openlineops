using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Topology.Domain.Capabilities;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Layouts;
using OpenLineOps.Topology.Domain.Slots;
using OpenLineOps.Topology.Domain.Systems;
using OpenLineOps.Topology.Domain.Topology;
using OpenLineOps.Topology.Infrastructure.Persistence;

namespace OpenLineOps.Topology.Tests;

public sealed class FileSystemProjectTopologyRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openlineops-topology", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RoundTripPersistsCurrentV1SystemsAndNestedLayoutContracts()
    {
        var scope = CreateScope();
        var topology = CreateTopology();
        var topologyRepository = new FileSystemProjectAutomationTopologyRepository();
        await topologyRepository.SaveAsync(scope, topology);

        var layout = CreateLayout();
        var layoutRepository = new FileSystemProjectSiteLayoutRepository();
        await layoutRepository.SaveAsync(scope, layout);

        var restoredTopology = await topologyRepository.GetByIdAsync(scope, topology.Id);
        var restoredLayout = await layoutRepository.GetByIdAsync(scope, layout.Id);
        Assert.IsType<StationSystem>(restoredTopology!.Systems.Single());
        Assert.Equal("station.eol", restoredTopology.Slots.Single().ParentSystemId.Value);
        Assert.Equal("element.group", restoredLayout!.Elements.Single(element =>
            element.Kind == LayoutElementKind.SlotShape).ParentElementId!.Value);

        var topologyJson = await File.ReadAllTextAsync(Directory.GetFiles(
            Path.Combine(scope.ApplicationRootPath, "topology"), "*.json").Single());
        Assert.Contains("\"schemaVersion\": \"openlineops.automation-topology\"", topologyJson, StringComparison.Ordinal);
        Assert.Contains("\"systems\"", topologyJson, StringComparison.Ordinal);
        Assert.DoesNotContain("equipmentNodes", topologyJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("automationModules", topologyJson, StringComparison.OrdinalIgnoreCase);

        var layoutJson = await File.ReadAllTextAsync(Directory.GetFiles(
            Path.Combine(scope.ApplicationRootPath, "layouts"), "*.json").Single());
        Assert.Contains("\"schemaVersion\": \"openlineops.site-layout\"", layoutJson, StringComparison.Ordinal);
        Assert.Contains("\"parentElementId\"", layoutJson, StringComparison.Ordinal);
        Assert.Contains("\"target\"", layoutJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownFieldsAreRejected()
    {
        var scope = CreateScope();
        var topology = CreateTopology();
        var repository = new FileSystemProjectAutomationTopologyRepository();
        await repository.SaveAsync(scope, topology);
        var path = Directory.GetFiles(Path.Combine(scope.ApplicationRootPath, "topology"), "*.json").Single();

        var currentWithUnknown = (await File.ReadAllTextAsync(path))
            .Replace("{", "{\n  \"unknown\": true,", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, currentWithUnknown);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.GetByIdAsync(scope, topology.Id));
    }

    [Fact]
    public async Task ReaderFailsClosedWhenGeneralSystemIsNestedUnderGeneralSystem()
    {
        var scope = CreateScope();
        var topology = CreateTopology();
        var repository = new FileSystemProjectAutomationTopologyRepository();
        await repository.SaveAsync(scope, topology);
        var path = Directory.GetFiles(Path.Combine(scope.ApplicationRootPath, "topology"), "*.json").Single();
        var invalidHierarchy = (await File.ReadAllTextAsync(path)).Replace(
            "\"systems\": [",
            """
            "systems": [
                {
                  "systemId": "system.root",
                  "parentSystemId": null,
                  "kind": "System",
                  "systemType": "root",
                  "displayName": "Root System",
                  "requiredCapabilityIds": [],
                  "providedCapabilityIds": [],
                  "metadata": {}
                },
                {
                  "systemId": "system.invalid-child",
                  "parentSystemId": "system.root",
                  "kind": "System",
                  "systemType": "component",
                  "displayName": "Invalid Child",
                  "requiredCapabilityIds": [],
                  "providedCapabilityIds": [],
                  "metadata": {}
                },
            """,
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, invalidHierarchy);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.GetByIdAsync(scope, topology.Id));
        Assert.Contains("Topology.ChildSystemRequiresStation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdatedAndCascadeDeletedStateSurvivesFreshRepositoryInstances()
    {
        var scope = CreateScope();
        var topology = CreateTopology();
        var layout = CreateLayout();
        Assert.True(topology.UpdateSystem(
            new AutomationSystemId("station.eol"),
            "station.final-test",
            "Final Test Station",
            new Dictionary<string, string> { ["line"] = "A" }).Succeeded);
        Assert.True(topology.UpdateSlot(
            new SlotDefinitionId("slot.1"),
            "UNIT-A1",
            "Production Unit Position A1",
            SlotMaterialKind.Carrier,
            false).Succeeded);
        Assert.Equal(2, layout.RemoveElementsByTargets(new HashSet<LayoutTargetReference>
        {
            new(LayoutTargetKind.SlotGroup, "group.fixture")
        }));
        Assert.True(topology.RemoveSlotGroup(new SlotGroupId("group.fixture")).Succeeded);

        await new FileSystemProjectAutomationTopologyRepository().SaveAsync(scope, topology);
        await new FileSystemProjectSiteLayoutRepository().SaveAsync(scope, layout);

        var restoredTopology = await new FileSystemProjectAutomationTopologyRepository()
            .GetByIdAsync(scope, topology.Id);
        var restoredLayout = await new FileSystemProjectSiteLayoutRepository()
            .GetByIdAsync(scope, layout.Id);
        var station = Assert.Single(restoredTopology!.Systems);
        Assert.Equal("Final Test Station", station.DisplayName);
        Assert.Equal("station.final-test", station.SystemType);
        Assert.Equal("A", station.Metadata["line"]);
        Assert.Empty(restoredTopology.SlotGroups);
        Assert.Empty(restoredTopology.Slots);
        Assert.Single(restoredLayout!.Elements);
        Assert.Equal("element.station", restoredLayout.Elements.Single().Id.Value);
    }

    private ProjectApplicationWorkspaceScope CreateScope()
    {
        Directory.CreateDirectory(_root);
        return new ProjectApplicationWorkspaceScope(
            "project",
            "app",
            _root,
            "applications/app/app.oloapp");
    }

    private static AutomationTopology CreateTopology()
    {
        var topology = AutomationTopology.Create(
            new AutomationTopologyId("topology.main"),
            "Main",
            new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero));
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
            "eol",
            "EOL",
            providedCapabilities: [capability.Id],
            metadata: new Dictionary<string, string>());
        Assert.True(topology.AddSystem(station).Succeeded);
        var group = SlotGroup.Create(
            new SlotGroupId("group.fixture"),
            station.Id,
            "Fixture",
            SlotGroupKind.FixtureNest,
            1);
        Assert.True(topology.AddSlotGroup(group).Succeeded);
        Assert.True(topology.AddSlot(SlotDefinition.Create(
            new SlotDefinitionId("slot.1"),
            group.Id,
            station.Id,
            "1",
            "Production Unit 1")).Succeeded);
        return topology;
    }

    private static SiteLayout CreateLayout()
    {
        var layout = SiteLayout.Create(
            new SiteLayoutId("layout.main"),
            new AutomationTopologyId("topology.main"),
            "Main",
            1000,
            600);
        var station = SiteLayoutElement.Create(
            new LayoutElementId("element.station"), LayoutElementKind.SystemShape,
            new LayoutTargetReference(LayoutTargetKind.System, "station.eol"),
            null, 100, 100, 400, 300, 0, 1);
        Assert.True(layout.AddElement(station).Succeeded);
        var group = SiteLayoutElement.Create(
            new LayoutElementId("element.group"), LayoutElementKind.GroupRegion,
            new LayoutTargetReference(LayoutTargetKind.SlotGroup, "group.fixture"),
            station.Id, 20, 20, 200, 120, 0, 2);
        Assert.True(layout.AddElement(group).Succeeded);
        Assert.True(layout.AddElement(SiteLayoutElement.Create(
            new LayoutElementId("element.slot"), LayoutElementKind.SlotShape,
            new LayoutTargetReference(LayoutTargetKind.Slot, "slot.1"),
            group.Id, 10, 10, 50, 40, 0, 3)).Succeeded);
        return layout;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
