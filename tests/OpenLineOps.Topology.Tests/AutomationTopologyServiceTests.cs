using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Topology.Application.Layouts;
using OpenLineOps.Topology.Application.Persistence;
using OpenLineOps.Topology.Application.ProjectWorkspaces;
using OpenLineOps.Topology.Application.Topologies;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Layouts;
using OpenLineOps.Topology.Domain.Topology;

namespace OpenLineOps.Topology.Tests;

public sealed class AutomationTopologyServiceTests
{
    [Fact]
    public async Task ProjectApplicationWorkflowPersistsSystemHierarchyAndNestedLayout()
    {
        var fixture = new Fixture();
        var service = fixture.Service;
        Assert.True((await service.CreateAsync(
            "project",
            "app",
            new CreateAutomationTopologyRequest("topology.main", "Main"))).IsSuccess);
        Assert.True((await service.AddCapabilityAsync(
            "project",
            "app",
            "topology.main",
            new AddCapabilityContractRequest(
                "test.execute", "Run", "1.0", null, null, 5, "Normal"))).IsSuccess);
        Assert.True((await service.AddSystemAsync(
            "project",
            "app",
            "topology.main",
            new AddAutomationSystemRequest(
                "station.eol",
                null,
                "Station",
                "vendor.eol",
                "EOL",
                [],
                ["test.execute"],
                new Dictionary<string, string> { ["vendor"] = "acme" }))).IsSuccess);
        Assert.True((await service.AddSlotGroupAsync(
            "project",
            "app",
            "topology.main",
            new AddSlotGroupRequest("group.fixture", "station.eol", "Fixture", "FixtureNest", 1))).IsSuccess);
        Assert.True((await service.AddSlotAsync(
            "project",
            "app",
            "topology.main",
            new AddSlotDefinitionRequest(
                "group.fixture", "slot.1", "station.eol", "1", "DUT 1", "Dut"))).IsSuccess);

        Assert.True((await service.CreateLayoutAsync(
            "project",
            "app",
            new CreateSiteLayoutRequest(
                "layout.main", "topology.main", "Main", 1000, 600, "mm"))).IsSuccess);
        Assert.True((await service.AddLayoutElementAsync(
            "project",
            "app",
            "layout.main",
            new AddSiteLayoutElementRequest(
                "element.station", "SystemShape", "System", "station.eol", null,
                100, 100, 400, 300, 0, 1, new Dictionary<string, string>()))).IsSuccess);
        Assert.True((await service.AddLayoutElementAsync(
            "project",
            "app",
            "layout.main",
            new AddSiteLayoutElementRequest(
                "element.group", "GroupRegion", "SlotGroup", "group.fixture", "element.station",
                20, 30, 200, 120, 0, 2, new Dictionary<string, string>()))).IsSuccess);
        var result = await service.AddLayoutElementAsync(
            "project",
            "app",
            "layout.main",
            new AddSiteLayoutElementRequest(
                "element.slot", "SlotShape", "Slot", "slot.1", "element.group",
                10, 10, 60, 40, 0, 3, new Dictionary<string, string>()));

        Assert.True(result.IsSuccess);
        Assert.Equal("element.group", result.Value.Elements.Single(element =>
            element.ElementId == "element.slot").ParentElementId);
        Assert.IsType<SortedDictionary<string, string>>(
            (await service.GetByIdAsync("project", "app", "topology.main"))
            .Value.Systems.Single().Metadata);
    }

    [Fact]
    public async Task LayoutRejectsGroupPlacedInsideDifferentStation()
    {
        var fixture = new Fixture();
        var service = fixture.Service;
        await service.CreateAsync("project", "app", new CreateAutomationTopologyRequest("topology", "Topology"));
        foreach (var id in new[] { "station.a", "station.b" })
        {
            Assert.True((await service.AddSystemAsync(
                "project", "app", "topology",
                new AddAutomationSystemRequest(
                    id, null, "Station", "station", id, [], [],
                    new Dictionary<string, string>()))).IsSuccess);
        }

        Assert.True((await service.AddSlotGroupAsync(
            "project", "app", "topology",
            new AddSlotGroupRequest("group.a", "station.a", "A", "FixtureNest", 1))).IsSuccess);
        await service.CreateLayoutAsync(
            "project", "app", new CreateSiteLayoutRequest("layout", "topology", "Layout", 500, 500, "mm"));
        await service.AddLayoutElementAsync(
            "project", "app", "layout",
            new AddSiteLayoutElementRequest(
                "station.b.shape", "SystemShape", "System", "station.b", null,
                0, 0, 300, 300, 0, 0, new Dictionary<string, string>()));

        var result = await service.AddLayoutElementAsync(
            "project", "app", "layout",
            new AddSiteLayoutElementRequest(
                "group.a.region", "GroupRegion", "SlotGroup", "group.a", "station.b.shape",
                10, 10, 100, 100, 0, 1, new Dictionary<string, string>()));

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Topology.LayoutGroupParentMismatch", result.Error.Code);
    }

    [Fact]
    public async Task ScopedCrudRenamesTargetsAndDeletesLayoutBeforeTopologyWithoutDanglingTargets()
    {
        var operations = new List<string>();
        var fixture = new Fixture(operations);
        var service = fixture.Service;
        await service.CreateAsync("project", "app", new CreateAutomationTopologyRequest("topology", "Topology"));
        await service.AddSystemAsync(
            "project", "app", "topology",
            new AddAutomationSystemRequest(
                "Station.Main", null, "Station", "station.old", "Old Station", [], [],
                new Dictionary<string, string>()));
        await service.AddSystemAsync(
            "project", "app", "topology",
            new AddAutomationSystemRequest(
                "System.Child", "Station.Main", "System", "component.old", "Old Child", [], [],
                new Dictionary<string, string>()));
        await service.AddSlotGroupAsync(
            "project", "app", "topology",
            new AddSlotGroupRequest("Group.Main", "Station.Main", "Old Group", "FixtureNest", 2));
        await service.AddSlotAsync(
            "project", "app", "topology",
            new AddSlotDefinitionRequest(
                "Group.Main", "Slot.Main", "Station.Main", "A1", "Old Slot", "Dut"));

        await service.CreateLayoutAsync(
            "project", "app", new CreateSiteLayoutRequest("layout", "topology", "Layout", 900, 600, "px"));
        await service.AddLayoutElementAsync(
            "project", "app", "layout",
            new AddSiteLayoutElementRequest(
                "station.shape", "SystemShape", "System", "Station.Main", null,
                20, 20, 400, 290, 0, 1, new Dictionary<string, string>()));
        await service.AddLayoutElementAsync(
            "project", "app", "layout",
            new AddSiteLayoutElementRequest(
                "child.shape", "SystemShape", "System", "System.Child", "station.shape",
                20, 58, 150, 56, 0, 2, new Dictionary<string, string>()));
        await service.AddLayoutElementAsync(
            "project", "app", "layout",
            new AddSiteLayoutElementRequest(
                "group.region", "GroupRegion", "SlotGroup", "Group.Main", "station.shape",
                20, 140, 180, 120, 0, 3, new Dictionary<string, string>()));
        await service.AddLayoutElementAsync(
            "project", "app", "layout",
            new AddSiteLayoutElementRequest(
                "slot.shape", "SlotShape", "Slot", "Slot.Main", "group.region",
                10, 40, 28, 26, 0, 4, new Dictionary<string, string>()));

        var updatedSystem = await service.UpdateSystemAsync(
            "project", "app", "topology", "Station.Main",
            new UpdateAutomationSystemRequest(
                "station.eol", "EOL Station", new Dictionary<string, string> { ["area"] = "EOL" }));
        var updatedGroup = await service.UpdateSlotGroupAsync(
            "project", "app", "topology", "Group.Main",
            new UpdateSlotGroupRequest("DUT Fixture", "TesterBank", 4));
        var updatedSlot = await service.UpdateSlotAsync(
            "project", "app", "topology", "Slot.Main",
            new UpdateSlotDefinitionRequest("A-01", "DUT Position", "Carrier", false));

        Assert.Equal("EOL Station", updatedSystem.Value.Systems.Single(system => system.SystemId == "Station.Main").DisplayName);
        Assert.Equal("DUT Fixture", updatedGroup.Value.SlotGroups.Single().DisplayName);
        Assert.Equal("A-01", updatedSlot.Value.Slots.Single().Address);
        Assert.False(updatedSlot.Value.Slots.Single().IsEnabled);
        var wrongCase = await service.DeleteSystemAsync(
            "project", "app", "topology", "system.child");
        Assert.True(wrongCase.IsFailure);
        Assert.Equal("NotFound.Topology.SystemNotFound", wrongCase.Error.Code);

        operations.Clear();
        var deletion = await service.DeleteSystemAsync(
            "project", "app", "topology", "System.Child");

        Assert.True(deletion.IsSuccess);
        Assert.Equal(1, deletion.Value.UpdatedLayoutCount);
        Assert.Equal(1, deletion.Value.RemovedLayoutElementCount);
        Assert.Contains("Production", deletion.Value.PublicationImpact, StringComparison.Ordinal);
        Assert.Equal(["layout:layout", "topology:topology"], operations);
        Assert.DoesNotContain(
            deletion.Value.Topology.Systems,
            system => system.SystemId == "System.Child");
        var layout = (await service.GetLayoutByIdAsync("project", "app", "layout")).Value;
        Assert.DoesNotContain(layout.Elements, element => element.TargetId == "System.Child");
        Assert.All(layout.Elements, element => Assert.NotEqual("child.shape", element.ParentElementId));
    }

    private sealed class Fixture
    {
        public Fixture(List<string>? operations = null)
        {
            var scope = new ProjectApplicationWorkspaceScope(
                "project", "app", Path.GetTempPath(), "applications/app/app.oloapp");
            Service = new ProjectAutomationTopologyService(
                new ScopeResolver(scope),
                new TopologyRepository(operations),
                new LayoutRepository(operations),
                new TestClock());
        }

        public IProjectAutomationTopologyService Service { get; }
    }

    private sealed class ScopeResolver(ProjectApplicationWorkspaceScope scope)
        : IProjectApplicationWorkspaceScopeResolver
    {
        public ValueTask<ProjectApplicationWorkspaceScope?> ResolveAsync(
            string projectId,
            string applicationId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<ProjectApplicationWorkspaceScope?>(
                projectId == scope.ProjectId && applicationId == scope.ApplicationId ? scope : null);
    }

    private sealed class TopologyRepository : IProjectAutomationTopologyRepository
    {
        private readonly Dictionary<string, AutomationTopology> _items = new(StringComparer.Ordinal);
        private readonly List<string>? _operations;

        public TopologyRepository(List<string>? operations)
        {
            _operations = operations;
        }

        public ValueTask SaveAsync(ProjectApplicationWorkspaceScope scope, AutomationTopology topology, CancellationToken cancellationToken = default)
        {
            _items[topology.Id.Value] = topology;
            _operations?.Add($"topology:{topology.Id.Value}");
            return ValueTask.CompletedTask;
        }

        public ValueTask<AutomationTopology?> GetByIdAsync(ProjectApplicationWorkspaceScope scope, AutomationTopologyId topologyId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_items.GetValueOrDefault(topologyId.Value));

        public ValueTask<IReadOnlyCollection<AutomationTopology>> ListAsync(ProjectApplicationWorkspaceScope scope, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyCollection<AutomationTopology>>(_items.Values.ToArray());
    }

    private sealed class LayoutRepository : IProjectSiteLayoutRepository
    {
        private readonly Dictionary<string, SiteLayout> _items = new(StringComparer.Ordinal);
        private readonly List<string>? _operations;

        public LayoutRepository(List<string>? operations)
        {
            _operations = operations;
        }

        public ValueTask SaveAsync(ProjectApplicationWorkspaceScope scope, SiteLayout layout, CancellationToken cancellationToken = default)
        {
            _items[layout.Id.Value] = layout;
            _operations?.Add($"layout:{layout.Id.Value}");
            return ValueTask.CompletedTask;
        }

        public ValueTask<SiteLayout?> GetByIdAsync(ProjectApplicationWorkspaceScope scope, SiteLayoutId layoutId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_items.GetValueOrDefault(layoutId.Value));

        public ValueTask<IReadOnlyCollection<SiteLayout>> ListByTopologyAsync(ProjectApplicationWorkspaceScope scope, AutomationTopologyId topologyId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyCollection<SiteLayout>>(_items.Values.Where(item => item.TopologyId == topologyId).ToArray());
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);
    }
}
