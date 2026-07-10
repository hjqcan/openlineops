using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Runner;

namespace OpenLineOps.Runner.Tests;

public sealed class RunnerSnapshotSelectorTests
{
    [Fact]
    public void SelectUsesActiveSnapshotByDefault()
    {
        var active = CreateSnapshot("snapshot.active");
        var project = CreateProject("snapshot.active", [CreateSnapshot("snapshot.old"), active]);

        var result = RunnerSnapshotSelector.Select(project, requestedSnapshot: null);

        Assert.True(result.IsSuccess);
        Assert.Same(active, result.Snapshot);
    }

    [Fact]
    public void SelectUsesExplicitSnapshotWithoutChangingActiveSnapshot()
    {
        var requested = CreateSnapshot("snapshot.requested");
        var project = CreateProject(
            "snapshot.active",
            [CreateSnapshot("snapshot.active"), requested]);

        var result = RunnerSnapshotSelector.Select(project, "snapshot.requested");

        Assert.True(result.IsSuccess);
        Assert.Same(requested, result.Snapshot);
    }

    [Fact]
    public void SelectRejectsProjectWithoutActiveSnapshot()
    {
        var result = RunnerSnapshotSelector.Select(
            CreateProject(activeSnapshotId: null, [CreateSnapshot("snapshot.available")]),
            "active");

        Assert.False(result.IsSuccess);
        Assert.Equal("Runner.ActiveSnapshotMissing", result.ErrorCode);
    }

    [Fact]
    public void SelectRejectsUnknownExplicitSnapshot()
    {
        var result = RunnerSnapshotSelector.Select(
            CreateProject("snapshot.active", [CreateSnapshot("snapshot.active")]),
            "snapshot.missing");

        Assert.False(result.IsSuccess);
        Assert.Equal("Runner.SnapshotNotFound", result.ErrorCode);
    }

    [Theory]
    [InlineData(" snapshot.active")]
    [InlineData("snapshot.active ")]
    [InlineData("")]
    public void SelectRejectsNonCanonicalSnapshotSelection(string requestedSnapshot)
    {
        var result = RunnerSnapshotSelector.Select(
            CreateProject("snapshot.active", [CreateSnapshot("snapshot.active")]),
            requestedSnapshot);

        Assert.False(result.IsSuccess);
        Assert.Equal("Runner.SnapshotSelectionInvalid", result.ErrorCode);
    }

    internal static AutomationProjectDetails CreateProject(
        string? activeSnapshotId,
        IReadOnlyCollection<PublishedProjectSnapshotDetails> snapshots)
    {
        return new AutomationProjectDetails(
            "project.line-a",
            "Line A",
            "C:/automation/line-a",
            new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero),
            activeSnapshotId,
            [new ProjectApplicationDetails(
                "application.main",
                "Main",
                "topology.main",
                ["process.main"],
                "applications/application.main/application.main.oloapp")],
            snapshots);
    }

    internal static PublishedProjectSnapshotDetails CreateSnapshot(
        string snapshotId,
        string releaseManifestPath = "releases/release-snapshot/release.json",
        string releaseContentSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
    {
        return new PublishedProjectSnapshotDetails(
            snapshotId,
            "project.line-a",
            "application.main",
            "topology.main",
            ["layout.main"],
            "line.main",
            new DateTimeOffset(2026, 7, 10, 1, 0, 0, TimeSpan.Zero),
            [],
            [],
            [],
            releaseManifestPath,
            releaseContentSha256);
    }
}
