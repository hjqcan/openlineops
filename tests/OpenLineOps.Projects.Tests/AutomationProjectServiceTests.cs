using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Infrastructure.Persistence;

namespace OpenLineOps.Projects.Tests;

public sealed class AutomationProjectServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ServiceCreatesProjectAddsApplicationLinksProcessAndPublishesSnapshot()
    {
        var repository = new InMemoryAutomationProjectRepository();
        var service = new AutomationProjectService(repository, new FixedClock(Now));

        var created = await service.CreateAsync(new CreateAutomationProjectRequest(
            "project.main",
            "Main Project",
            "projects/main"));
        var withApplication = await service.AddApplicationAsync(
            "project.main",
            new AddProjectApplicationRequest("application.main", "Main Station"));
        var withTopology = await service.LinkTopologyAsync(
            "project.main",
            new LinkProjectTopologyRequest("application.main", "topology.main"));
        var withProcess = await service.LinkProcessDefinitionAsync(
            "project.main",
            new LinkProjectProcessDefinitionRequest("application.main", "process.main"));
        var published = await service.PublishSnapshotAsync(
            "project.main",
            new PublishProjectSnapshotRequest(
                "snapshot.main.v1",
                "application.main",
                "topology.main",
                ["layout.main"],
                "line.main",
                [
                    new SnapshotCapabilityBindingRequest(
                        "motion.axis.move",
                        "binding.axis.x.simulator",
                        "Simulator",
                        "simulator.axis.x",
                        "station.main",
                        "station.main")
                ],
                [new ProjectTargetReferenceRequest("slot", "slot.left-nest.1")],
                ["block.move-axis@1.0.0"],
                "releases/release-main/release.json",
                new string('a', 64)));

        Assert.True(created.IsSuccess);
        Assert.True(withApplication.IsSuccess);
        Assert.True(withTopology.IsSuccess);
        Assert.True(withProcess.IsSuccess);
        Assert.True(published.IsSuccess);
        Assert.Equal("snapshot.main.v1", published.Value.ActiveSnapshotId);
        Assert.Single(published.Value.Applications);
        Assert.Single(published.Value.Snapshots);
        Assert.True(repository.SaveCount >= 5);
    }

    [Fact]
    public async Task ServiceRejectsDuplicateProjectIds()
    {
        var service = new AutomationProjectService(
            new InMemoryAutomationProjectRepository(),
            new FixedClock(Now));
        var request = new CreateAutomationProjectRequest("project.main", "Main Project", "projects/main");

        var first = await service.CreateAsync(request);
        var duplicate = await service.CreateAsync(request);

        Assert.True(first.IsSuccess);
        Assert.True(duplicate.IsFailure);
        Assert.Equal("Conflict.Projects.ProjectAlreadyExists", duplicate.Error.Code);
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
