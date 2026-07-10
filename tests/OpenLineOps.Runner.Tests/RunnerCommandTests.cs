using System.Text.Json;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Application.Runtime;
using OpenLineOps.Projects.Api.Integrations;
using OpenLineOps.Projects.Application.ProjectWorkspaces;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Runner;

namespace OpenLineOps.Runner.Tests;

public sealed class RunnerCommandTests
{
    [Fact]
    public async Task RunUsesActiveImmutableSnapshotAndForwardsTraceMetadata()
    {
        var snapshot = RunnerSnapshotSelectorTests.CreateSnapshot("snapshot.active");
        var workspace = CreateWorkspace(
            RunnerSnapshotSelectorTests.CreateProject("snapshot.active", [snapshot]),
            snapshot);
        var launcher = new CapturingRuntimeLauncher(Result.Success(
            new StartedProcessRuntimeSessionDetails(
                Guid.Parse("00000000-0000-0000-0000-000000000042"),
                snapshot.ConfigurationSnapshotId,
                "Completed",
                CompletedSteps: 3,
                CommandCount: 3,
                IncidentCount: 0)));
        var command = new RunnerCommand(new StubWorkspaceService(workspace), launcher);
        var writer = new StringWriter();

        var exitCode = await command.RunAsync(
            new RunnerRunOptions(
                "project",
                "active",
                "SN-42",
                "batch-a",
                "fixture-a",
                "device-a",
                "actor-a"),
            "C:/automation",
            writer);

        Assert.Equal(RunnerExitCodes.Success, exitCode);
        Assert.Same(snapshot, launcher.Snapshot);
        Assert.NotNull(launcher.Request);
        Assert.Equal(snapshot.ConfigurationSnapshotId, launcher.Request.ConfigurationSnapshotId);
        Assert.Equal("SN-42", launcher.Request.SerialNumber);
        Assert.Equal("batch-a", launcher.Request.BatchId);
        Assert.Equal("fixture-a", launcher.Request.FixtureId);
        Assert.Equal("device-a", launcher.Request.DeviceId);
        Assert.Equal("actor-a", launcher.Request.ActorId);

        using var document = JsonDocument.Parse(writer.ToString());
        var root = document.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("snapshot.active", root.GetProperty("project").GetProperty("snapshotId").GetString());
        Assert.Equal("Completed", root.GetProperty("session").GetProperty("status").GetString());
        Assert.False(root.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task RunReturnsRuntimeFailureExitCodeForFailedTerminalSession()
    {
        var snapshot = RunnerSnapshotSelectorTests.CreateSnapshot("snapshot.active");
        var workspace = CreateWorkspace(
            RunnerSnapshotSelectorTests.CreateProject("snapshot.active", [snapshot]),
            snapshot);
        var launcher = new CapturingRuntimeLauncher(Result.Success(
            new StartedProcessRuntimeSessionDetails(
                Guid.Parse("00000000-0000-0000-0000-000000000043"),
                snapshot.ConfigurationSnapshotId,
                "Failed",
                CompletedSteps: 1,
                CommandCount: 2,
                IncidentCount: 1)));
        var command = new RunnerCommand(new StubWorkspaceService(workspace), launcher);
        var writer = new StringWriter();

        var exitCode = await command.RunAsync(
            new RunnerRunOptions("project", "active", null, null, null, null, null),
            "C:/automation",
            writer);

        Assert.Equal(6, exitCode);
        using var document = JsonDocument.Parse(writer.ToString());
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("Failed", document.RootElement.GetProperty("session").GetProperty("status").GetString());
    }

    private static AutomationProjectWorkspaceDetails CreateWorkspace(
        AutomationProjectDetails project,
        PublishedProjectSnapshotDetails snapshot)
    {
        var manifestSnapshot = new PublishedProjectSnapshotManifest(
            snapshot.SnapshotId,
            snapshot.ProjectId,
            snapshot.ApplicationId,
            snapshot.TopologyId,
            snapshot.LayoutIds.ToArray(),
            snapshot.ProcessDefinitionId,
            snapshot.ProcessVersionId,
            snapshot.ConfigurationSnapshotId,
            snapshot.PublishedAtUtc,
            [],
            [],
            snapshot.BlockVersionIds.ToArray(),
            snapshot.ReleaseManifestPath,
            snapshot.ReleaseContentSha256);
        var manifest = new AutomationProjectManifest(
            AutomationProjectManifest.CurrentFormatVersion,
            AutomationProjectManifest.ProductName,
            project.ProjectId,
            project.DisplayName,
            project.ProjectPath,
            project.CreatedAtUtc,
            new DateTimeOffset(2026, 7, 10, 2, 0, 0, TimeSpan.Zero),
            project.ActiveSnapshotId,
            [new ProjectApplicationManifest(
                "application.main",
                "Main",
                "topology.main",
                ["process.main"],
                "applications/application.main/application.main.oloapp")],
            [manifestSnapshot]);

        return new AutomationProjectWorkspaceDetails(
            project,
            Path.Combine(
                project.ProjectPath,
                AutomationProjectFileConvention.GetProjectFileName(project.ProjectId)),
            manifest);
    }

    private sealed class StubWorkspaceService(AutomationProjectWorkspaceDetails workspace)
        : IAutomationProjectWorkspaceService
    {
        public Task<Result<AutomationProjectWorkspaceDetails>> CreateAsync(
            CreateAutomationProjectWorkspaceRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<AutomationProjectWorkspaceDetails>> OpenAsync(
            OpenAutomationProjectWorkspaceRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result.Success(workspace));
        }

        public Task<Result<AutomationProjectWorkspaceDetails>> SaveManifestAsync(
            string projectId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<AutomationProjectWorkspaceDetails>> ImportApplicationAsync(
            string projectId,
            ImportAutomationProjectApplicationRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class CapturingRuntimeLauncher(
        Result<StartedProcessRuntimeSessionDetails> result)
        : IProjectReleaseRuntimeSessionLauncher
    {
        public PublishedProjectSnapshotDetails? Snapshot { get; private set; }

        public StartProcessRuntimeSessionRequest? Request { get; private set; }

        public ValueTask<Result<StartedProcessRuntimeSessionDetails>> StartAsync(
            PublishedProjectSnapshotDetails snapshot,
            StartProcessRuntimeSessionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshot = snapshot;
            Request = request;
            return ValueTask.FromResult(result);
        }
    }
}
