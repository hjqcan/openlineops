using OpenLineOps.Projects.Domain.Applications;
using OpenLineOps.Projects.Domain.Events;
using OpenLineOps.Projects.Domain.Identifiers;
using OpenLineOps.Projects.Domain.Projects;
using OpenLineOps.Projects.Domain.Snapshots;

namespace OpenLineOps.Projects.Tests;

public sealed class AutomationProjectTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 7, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PublishedAtUtc = new(2026, 7, 9, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PublishSnapshotFreezesLinkedApplicationTopologyProcessBindingsAndTargets()
    {
        var project = CreateProjectWithApplication(out var applicationId);
        var topologyId = new AutomationTopologyId("topology.main");
        var processDefinitionId = new ProcessDefinitionId("process.main");
        var processVersionId = new ProcessVersionId("process.main.v1");
        var configurationSnapshotId = new ConfigurationSnapshotId("configuration.main.v1");
        var snapshotId = new PublishedProjectSnapshotId("snapshot.main.v1");

        Assert.True(project.LinkTopology(applicationId, topologyId).Succeeded);
        Assert.True(project.LinkProcessDefinition(applicationId, processDefinitionId).Succeeded);

        var result = project.PublishSnapshot(
            snapshotId,
            applicationId,
            topologyId,
            processDefinitionId,
            processVersionId,
            configurationSnapshotId,
            [
                new SnapshotCapabilityBinding(
                    "motion.axis.move",
                    "binding.axis.x.simulator",
                    "Simulator",
                    "simulator.axis.x")
            ],
            [new ProjectTargetReference("slot", "slot.left-nest.1")],
            ["block.move-axis@1.0.0", "block.move-axis@1.0.0"],
            PublishedAtUtc);

        Assert.True(result.Succeeded);
        Assert.Equal(snapshotId, project.ActiveSnapshotId);
        var snapshot = Assert.Single(project.Snapshots);
        Assert.Equal(project.Id, snapshot.ProjectId);
        Assert.Equal(applicationId, snapshot.ApplicationId);
        Assert.Equal(topologyId, snapshot.TopologyId);
        Assert.Equal(processDefinitionId, snapshot.ProcessDefinitionId);
        Assert.Equal(processVersionId, snapshot.ProcessVersionId);
        Assert.Equal(configurationSnapshotId, snapshot.ConfigurationSnapshotId);
        Assert.Single(snapshot.CapabilityBindings);
        Assert.Single(snapshot.TargetReferences);
        Assert.Single(snapshot.BlockVersionIds);
        Assert.Contains(project.DomainEvents, domainEvent =>
            domainEvent is ProjectSnapshotPublishedDomainEvent published
            && published.ProjectId == project.Id
            && published.SnapshotId == snapshotId
            && published.ApplicationId == applicationId
            && published.TopologyId == topologyId);
    }

    [Fact]
    public void PublishSnapshotRejectsUnlinkedTopology()
    {
        var project = CreateProjectWithApplication(out var applicationId);
        var processDefinitionId = new ProcessDefinitionId("process.main");

        Assert.True(project.LinkProcessDefinition(applicationId, processDefinitionId).Succeeded);

        var result = project.PublishSnapshot(
            new PublishedProjectSnapshotId("snapshot.main.v1"),
            applicationId,
            new AutomationTopologyId("topology.main"),
            processDefinitionId,
            new ProcessVersionId("process.main.v1"),
            new ConfigurationSnapshotId("configuration.main.v1"),
            [new SnapshotCapabilityBinding("motion.axis.move", "binding.axis.x.simulator", "Simulator", "simulator.axis.x")],
            [new ProjectTargetReference("slot", "slot.left-nest.1")],
            ["block.move-axis@1.0.0"],
            PublishedAtUtc);

        Assert.False(result.Succeeded);
        Assert.Equal("Projects.TopologyNotLinked", result.Code);
    }

    [Fact]
    public void PublishSnapshotRejectsMissingCapabilityBindings()
    {
        var project = CreateProjectWithApplication(out var applicationId);
        var topologyId = new AutomationTopologyId("topology.main");
        var processDefinitionId = new ProcessDefinitionId("process.main");

        Assert.True(project.LinkTopology(applicationId, topologyId).Succeeded);
        Assert.True(project.LinkProcessDefinition(applicationId, processDefinitionId).Succeeded);

        var result = project.PublishSnapshot(
            new PublishedProjectSnapshotId("snapshot.main.v1"),
            applicationId,
            topologyId,
            processDefinitionId,
            new ProcessVersionId("process.main.v1"),
            new ConfigurationSnapshotId("configuration.main.v1"),
            [],
            [new ProjectTargetReference("slot", "slot.left-nest.1")],
            ["block.move-axis@1.0.0"],
            PublishedAtUtc);

        Assert.False(result.Succeeded);
        Assert.Equal("Projects.NoCapabilityBindings", result.Code);
    }

    [Fact]
    public void AddApplicationRejectsDuplicateDisplayNames()
    {
        var project = AutomationProject.Create(
            new AutomationProjectId("project.main"),
            "Main Project",
            "projects/main",
            CreatedAtUtc);
        var first = ProjectApplication.Create(new ProjectApplicationId("application.main"), "Station Application");
        var second = ProjectApplication.Create(new ProjectApplicationId("application.secondary"), "station application");

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
        var application = ProjectApplication.Create(applicationId, "Station Application");

        Assert.True(project.AddApplication(application).Succeeded);

        return project;
    }
}
