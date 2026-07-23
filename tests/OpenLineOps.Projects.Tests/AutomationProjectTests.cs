using OpenLineOps.Projects.Domain.Applications;
using OpenLineOps.Projects.Domain.Identifiers;
using OpenLineOps.Projects.Domain.Projects;
using OpenLineOps.Projects.Domain.Snapshots;

namespace OpenLineOps.Projects.Tests;

public sealed class AutomationProjectTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 7, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PublishedAtUtc = new(2026, 7, 9, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PublishSnapshotFreezesProductionLineTopologyBindingsAndTargets()
    {
        var project = CreateProjectWithApplication(out var applicationId);
        var topologyId = new AutomationTopologyId("topology.main");
        var snapshotId = new PublishedProjectSnapshotId("snapshot.main");

        Assert.True(project.LinkTopology(applicationId, topologyId).Succeeded);

        var result = project.PublishSnapshot(
            snapshotId,
            applicationId,
            topologyId,
            ["layout.main"],
            new ProductionLineDefinitionId("line.main"),
            [
                new SnapshotCapabilityBinding(
                    "motion.axis.move",
                    "binding.axis.x.simulator",
                    "Simulator",
                    "simulator.axis.x",
                    "station.main",
                    "station.main")
            ],
            [new ProjectTargetReference("slot", "slot.left-nest.1")],
            ["block.move-axis@1.0.0"],
            "releases/release-main/release.json",
            new string('a', 64),
            PublishedAtUtc);

        Assert.True(result.Succeeded);
        Assert.Equal(snapshotId, project.ActiveSnapshotId);
        var snapshot = Assert.Single(project.Snapshots);
        Assert.Equal(project.Id, snapshot.ProjectId);
        Assert.Equal(applicationId, snapshot.ApplicationId);
        Assert.Equal(topologyId, snapshot.TopologyId);
        Assert.Equal("layout.main", Assert.Single(snapshot.LayoutIds));
        Assert.Equal("line.main", snapshot.ProductionLineDefinitionId.Value);
        Assert.Single(snapshot.CapabilityBindings);
        Assert.Single(snapshot.TargetReferences);
        Assert.Single(snapshot.BlockVersionIds);
        Assert.Equal("releases/release-main/release.json", snapshot.ReleaseManifestPath);
        Assert.Equal(new string('a', 64), snapshot.ReleaseContentSha256);
    }

    [Fact]
    public void PublishSnapshotRejectsUnlinkedTopology()
    {
        var project = CreateProjectWithApplication(out var applicationId);
        var result = project.PublishSnapshot(
            new PublishedProjectSnapshotId("snapshot.main"),
            applicationId,
            new AutomationTopologyId("topology.main"),
            ["layout.main"],
            new ProductionLineDefinitionId("line.main"),
            [new SnapshotCapabilityBinding("motion.axis.move", "binding.axis.x.simulator", "Simulator", "simulator.axis.x", "station.main", "station.main")],
            [new ProjectTargetReference("slot", "slot.left-nest.1")],
            ["block.move-axis@1.0.0"],
            "releases/release-main/release.json",
            new string('a', 64),
            PublishedAtUtc);

        Assert.False(result.Succeeded);
        Assert.Equal("Projects.TopologyNotLinked", result.Code);
    }

    [Fact]
    public void PublishSnapshotRejectsMissingCapabilityBindings()
    {
        var project = CreateProjectWithApplication(out var applicationId);
        var topologyId = new AutomationTopologyId("topology.main");
        Assert.True(project.LinkTopology(applicationId, topologyId).Succeeded);

        var result = project.PublishSnapshot(
            new PublishedProjectSnapshotId("snapshot.main"),
            applicationId,
            topologyId,
            ["layout.main"],
            new ProductionLineDefinitionId("line.main"),
            [],
            [new ProjectTargetReference("slot", "slot.left-nest.1")],
            ["block.move-axis@1.0.0"],
            "releases/release-main/release.json",
            new string('a', 64),
            PublishedAtUtc);

        Assert.False(result.Succeeded);
        Assert.Equal("Projects.NoCapabilityBindings", result.Code);
    }

    [Fact]
    public void PublishSnapshotRejectsMissingFrozenLayouts()
    {
        var project = CreateProjectWithApplication(out var applicationId);
        var topologyId = new AutomationTopologyId("topology.main");
        Assert.True(project.LinkTopology(applicationId, topologyId).Succeeded);

        var result = project.PublishSnapshot(
            new PublishedProjectSnapshotId("snapshot.main"),
            applicationId,
            topologyId,
            [],
            new ProductionLineDefinitionId("line.main"),
            [new SnapshotCapabilityBinding(
                "motion.axis.move",
                "binding.axis.x.simulator",
                "Simulator",
                "simulator.axis.x",
                "station.main",
                "station.main")],
            [new ProjectTargetReference("slot", "slot.left-nest.1")],
            ["block.move-axis@1.0.0"],
            "releases/release-main/release.json",
            new string('a', 64),
            PublishedAtUtc);

        Assert.False(result.Succeeded);
        Assert.Equal("Projects.NoLayouts", result.Code);
    }

    [Fact]
    public void PublishedSnapshotRejectsNonCanonicalOrDuplicateLayoutIds()
    {
        string[][] invalidCollections =
        [
            [null!],
            [string.Empty],
            [" "],
            [" layout.main"],
            ["layout.main "],
            ["layout.main", "layout.main"]
        ];

        foreach (var layoutIds in invalidCollections)
        {
            Assert.Throws<ArgumentException>(() => PublishSnapshot(layoutIds, ["block.main@1"]));
        }
    }

    [Fact]
    public void PublishedSnapshotRejectsNonCanonicalOrDuplicateBlockVersionIds()
    {
        string[][] invalidCollections =
        [
            [null!],
            [string.Empty],
            [" "],
            [" block.main@1"],
            ["block.main@1 "],
            ["block.main@1", "block.main@1"]
        ];

        foreach (var blockVersionIds in invalidCollections)
        {
            Assert.Throws<ArgumentException>(() => PublishSnapshot(["layout.main"], blockVersionIds));
        }
    }

    [Fact]
    public void AddApplicationRejectsDuplicateDisplayNames()
    {
        var project = AutomationProject.Create(
            new AutomationProjectId("project.main"),
            "Main Project",
            "projects/main",
            CreatedAtUtc);
        var first = ProjectApplication.Create(
            new ProjectApplicationId("application.main"),
            "Station Application",
            "applications/application.main/application.main.oloapp");
        var second = ProjectApplication.Create(
            new ProjectApplicationId("application.secondary"),
            "station application",
            "applications/application.secondary/application.secondary.oloapp");

        Assert.True(project.AddApplication(first).Succeeded);
        var result = project.AddApplication(second);

        Assert.False(result.Succeeded);
        Assert.Equal("Projects.ApplicationNameAlreadyExists", result.Code);
    }

    private static AutomationProject CreateProjectWithApplication(out ProjectApplicationId applicationId)
    {
        var project = AutomationProject.Create(
            new AutomationProjectId("project.main"),
            "Main Project",
            "projects/main",
            CreatedAtUtc);
        applicationId = new ProjectApplicationId("application.main");
        var application = ProjectApplication.Create(
            applicationId,
            "Station Application",
            "applications/application.main/application.main.oloapp");

        Assert.True(project.AddApplication(application).Succeeded);

        return project;
    }

    private static PublishedProjectSnapshot PublishSnapshot(
        IEnumerable<string> layoutIds,
        IEnumerable<string> blockVersionIds)
    {
        return PublishedProjectSnapshot.Publish(
            new PublishedProjectSnapshotId("snapshot.main"),
            new AutomationProjectId("project.main"),
            new ProjectApplicationId("application.main"),
            new AutomationTopologyId("topology.main"),
            layoutIds,
            new ProductionLineDefinitionId("line.main"),
            [new SnapshotCapabilityBinding("capability.main", "binding.main", "Simulator", "simulator.main", "station.main", "station.main")],
            [new ProjectTargetReference("System", "station.main")],
            blockVersionIds,
            "releases/snapshot.main/release.json",
            new string('a', 64),
            PublishedAtUtc);
    }
}
