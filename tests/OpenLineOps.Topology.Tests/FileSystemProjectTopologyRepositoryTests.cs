using System.Text.Json;
using System.Text.Json.Nodes;
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
            _projectDirectory,
            "applications/application.a/application.a.oloapp");
        var applicationB = new ProjectApplicationWorkspaceScope(
            "project.shared",
            "application.b",
            _projectDirectory,
            "applications/application.b/application.b.oloapp");
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

    [Fact]
    public async Task EditableResourcesAreStoredBesideTheApplicationProjectFile()
    {
        var scope = new ProjectApplicationWorkspaceScope(
            "project.custom-root",
            "application.id-does-not-name-the-folder",
            _projectDirectory,
            "applications/Operator Cell/Main Line.oloapp");
        var topologyId = new AutomationTopologyId("topology.custom/root");
        var layoutId = new SiteLayoutId("layout.custom/root");
        var topology = CreateCompleteTopology(topologyId, "Custom Root Topology");
        var layout = CreateCompleteLayout(layoutId, topologyId, x: 240);

        await new FileSystemProjectAutomationTopologyRepository().SaveAsync(scope, topology);
        await new FileSystemProjectSiteLayoutRepository().SaveAsync(scope, layout);

        var topologyPath = Assert.Single(Directory.GetFiles(
            Path.Combine(scope.ApplicationRootPath, "topology"),
            "topology-*.json"));
        var layoutPath = Assert.Single(Directory.GetFiles(
            Path.Combine(scope.ApplicationRootPath, "layouts"),
            "layout-*.json"));

        Assert.Equal(
            Path.Combine(scope.ApplicationRootPath, "topology"),
            Path.GetDirectoryName(topologyPath));
        Assert.Equal(
            Path.Combine(scope.ApplicationRootPath, "layouts"),
            Path.GetDirectoryName(layoutPath));
        Assert.StartsWith("topology-topology.custom-root--", Path.GetFileName(topologyPath));
        Assert.StartsWith("layout-layout.custom-root--", Path.GetFileName(layoutPath));
    }

    [Fact]
    public async Task CompleteApplicationFolderCanBeCopiedBetweenProjectsWithoutRewritingTopologyResources()
    {
        const string applicationId = "application.portable";
        var sourceScope = new ProjectApplicationWorkspaceScope(
            "project-a",
            applicationId,
            Path.Combine(_projectDirectory, "project-a"),
            "applications/Authoring Cell/application.oloapp");
        var targetScope = new ProjectApplicationWorkspaceScope(
            "project-b",
            applicationId,
            Path.Combine(_projectDirectory, "project-b"),
            "applications/Portable Cell/application.oloapp");
        var topologyId = new AutomationTopologyId("topology.portable");
        var layoutId = new SiteLayoutId("layout.portable");

        Directory.CreateDirectory(sourceScope.ApplicationRootPath);
        await File.WriteAllBytesAsync(sourceScope.ApplicationProjectFilePath, [0x4f, 0x4c, 0x4f]);

        await new FileSystemProjectAutomationTopologyRepository().SaveAsync(
            sourceScope,
            CreateCompleteTopology(topologyId, "Portable Topology"));
        await new FileSystemProjectSiteLayoutRepository().SaveAsync(
            sourceScope,
            CreateCompleteLayout(layoutId, topologyId, x: 360));

        var topologyPath = Assert.Single(Directory.GetFiles(
            Path.Combine(sourceScope.ApplicationRootPath, "topology"),
            "topology-*.json"));
        var layoutPath = Assert.Single(Directory.GetFiles(
            Path.Combine(sourceScope.ApplicationRootPath, "layouts"),
            "layout-*.json"));
        AssertPortableV2Document(topologyPath, applicationId);
        AssertPortableV2Document(layoutPath, applicationId);

        CopyDirectoryByteForByte(sourceScope.ApplicationRootPath, targetScope.ApplicationRootPath);
        AssertApplicationFoldersHaveEqualBytes(sourceScope.ApplicationRootPath, targetScope.ApplicationRootPath);

        var restoredTopology = await new FileSystemProjectAutomationTopologyRepository()
            .GetByIdAsync(targetScope, topologyId);
        var restoredLayout = await new FileSystemProjectSiteLayoutRepository()
            .GetByIdAsync(targetScope, layoutId);

        Assert.NotNull(restoredTopology);
        Assert.Equal("Portable Topology", restoredTopology.DisplayName);
        Assert.Equal(3, restoredTopology.Nodes.Count);
        Assert.Single(restoredTopology.Modules);
        Assert.Single(restoredTopology.DriverBindings);
        Assert.Single(restoredTopology.Slots);
        Assert.NotNull(restoredLayout);
        Assert.Equal(2, restoredLayout.Elements.Count);
        Assert.Equal(360, restoredLayout.Elements.Single(element => element.Id.Value == "element.station").X);
    }

    [Fact]
    public async Task PortableResourcesStillRejectADifferentApplicationIdentity()
    {
        var writerScope = new ProjectApplicationWorkspaceScope(
            "project-a",
            "application-a",
            _projectDirectory,
            "applications/Shared Cell/application.oloapp");
        var wrongApplicationScope = new ProjectApplicationWorkspaceScope(
            "project-b",
            "application-b",
            _projectDirectory,
            "applications/Shared Cell/application.oloapp");
        var topologyId = new AutomationTopologyId("topology.identity");
        var layoutId = new SiteLayoutId("layout.identity");

        await new FileSystemProjectAutomationTopologyRepository().SaveAsync(
            writerScope,
            CreateCompleteTopology(topologyId, "Identity Topology"));
        await new FileSystemProjectSiteLayoutRepository().SaveAsync(
            writerScope,
            CreateCompleteLayout(layoutId, topologyId, x: 600));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new FileSystemProjectAutomationTopologyRepository()
                .GetByIdAsync(wrongApplicationScope, topologyId));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new FileSystemProjectSiteLayoutRepository()
                .GetByIdAsync(wrongApplicationScope, layoutId));
    }

    [Fact]
    public async Task ObsoleteTopologyFormatIsRejected()
    {
        var scope = new ProjectApplicationWorkspaceScope(
            "project-current-only",
            "application-current-only",
            _projectDirectory,
            "applications/current-only/current-only.oloapp");
        var topologyId = new AutomationTopologyId("topology.current-only");
        var repository = new FileSystemProjectAutomationTopologyRepository();
        await repository.SaveAsync(scope, CreateCompleteTopology(topologyId, "Current Only"));

        var path = Assert.Single(Directory.GetFiles(
            scope.ApplicationRootPath,
            "topology-*.json",
            SearchOption.AllDirectories));
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        document["formatVersion"] = 1;
        await File.WriteAllTextAsync(path, document.ToJsonString());

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.GetByIdAsync(scope, topologyId));
    }

    [Fact]
    public async Task RemovedHostProjectIdFieldIsRejectedFromTopologyResource()
    {
        var scope = new ProjectApplicationWorkspaceScope(
            "project-strict",
            "application-strict",
            _projectDirectory,
            "applications/strict/strict.oloapp");
        var topologyId = new AutomationTopologyId("topology.strict");
        var repository = new FileSystemProjectAutomationTopologyRepository();
        await repository.SaveAsync(scope, CreateCompleteTopology(topologyId, "Strict"));

        var path = Assert.Single(Directory.GetFiles(
            scope.ApplicationRootPath,
            "topology-*.json",
            SearchOption.AllDirectories));
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        document["projectId"] = "removed-host-project";
        await File.WriteAllTextAsync(path, document.ToJsonString());

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.GetByIdAsync(scope, topologyId));
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

    private static void AssertPortableV2Document(string path, string applicationId)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        Assert.Equal(2, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal(applicationId, root.GetProperty("applicationId").GetString());
        Assert.False(root.TryGetProperty("projectId", out _));
    }

    private static void CopyDirectoryByteForByte(string sourceDirectory, string targetDirectory)
    {
        foreach (var directory in Directory.EnumerateDirectories(
                     sourceDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                targetDirectory,
                Path.GetRelativePath(sourceDirectory, directory)));
        }

        Directory.CreateDirectory(targetDirectory);
        foreach (var sourcePath in Directory.EnumerateFiles(
                     sourceDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            var targetPath = Path.Combine(
                targetDirectory,
                Path.GetRelativePath(sourceDirectory, sourcePath));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath);
        }
    }

    private static void AssertApplicationFoldersHaveEqualBytes(
        string sourceDirectory,
        string targetDirectory)
    {
        var relativePaths = Directory.EnumerateFiles(
                sourceDirectory,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(sourceDirectory, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(relativePaths);
        Assert.Equal(
            relativePaths,
            Directory.EnumerateFiles(targetDirectory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(targetDirectory, path))
                .Order(StringComparer.Ordinal)
                .ToArray());

        foreach (var relativePath in relativePaths)
        {
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(sourceDirectory, relativePath)),
                File.ReadAllBytes(Path.Combine(targetDirectory, relativePath)));
        }
    }
}
