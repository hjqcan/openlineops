using System.Text.Json;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Projects.Api.Integrations;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.ProjectWorkspaces;
using OpenLineOps.Runner;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Application.Recovery;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runner.Tests;

public sealed class RunnerCommandTests
{
    private static readonly Guid RunId =
        Guid.Parse("00000000-0000-0000-0000-000000000042");

    [Fact]
    public async Task RunUsesActiveImmutableProductionLineAndForwardsRunIdentity()
    {
        var snapshot = RunnerSnapshotSelectorTests.CreateSnapshot("snapshot.active");
        var workspace = CreateWorkspace(
            RunnerSnapshotSelectorTests.CreateProject("snapshot.active", [snapshot]),
            snapshot);
        var launcher = new CapturingProductionRunLauncher(Result.Success(
            new ProductionRunRunResult(CreateProductionRun(ProductionRunStatus.Completed))));
        var command = CreateCommand(workspace, launcher);
        var writer = new StringWriter();

        var exitCode = await command.RunAsync(
            new RunnerRunOptions(
                "project",
                "active",
                RunId,
                "DUT-42",
                "batch-a",
                "fixture-a",
                "device-a",
                "actor-a"),
            "C:/automation",
            writer);

        Assert.Equal(RunnerExitCodes.Success, exitCode);
        Assert.Same(snapshot, launcher.Snapshot);
        Assert.NotNull(launcher.Request);
        Assert.Equal(RunId, launcher.Request.ProductionRunId);
        Assert.Equal("DUT-42", launcher.Request.DutIdentityValue);
        Assert.Equal("batch-a", launcher.Request.BatchId);
        Assert.Equal("fixture-a", launcher.Request.FixtureId);
        Assert.Equal("device-a", launcher.Request.DeviceId);
        Assert.Equal("actor-a", launcher.Request.ActorId);

        using var document = JsonDocument.Parse(writer.ToString());
        var root = document.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("snapshot.active", root.GetProperty("project").GetProperty("snapshotId").GetString());
        var productionRun = root.GetProperty("productionRun");
        Assert.Equal(RunId, productionRun.GetProperty("productionRunId").GetGuid());
        Assert.Equal("Completed", productionRun.GetProperty("status").GetString());
        Assert.Equal(1, productionRun.GetProperty("stageCount").GetInt32());
        Assert.Equal(1, productionRun.GetProperty("completedStageCount").GetInt32());
        Assert.Equal("stage.main", productionRun.GetProperty("stages")[0].GetProperty("stageId").GetString());
        Assert.False(root.TryGetProperty("error", out _));
        Assert.False(root.TryGetProperty("session", out _));
    }

    [Fact]
    public async Task RunReturnsProductionRunFailureExitCodeForFailedTerminalRun()
    {
        var snapshot = RunnerSnapshotSelectorTests.CreateSnapshot("snapshot.active");
        var workspace = CreateWorkspace(
            RunnerSnapshotSelectorTests.CreateProject("snapshot.active", [snapshot]),
            snapshot);
        var launcher = new CapturingProductionRunLauncher(Result.Success(
            new ProductionRunRunResult(CreateProductionRun(ProductionRunStatus.Failed))));
        var command = CreateCommand(workspace, launcher);
        var writer = new StringWriter();

        var exitCode = await command.RunAsync(
            new RunnerRunOptions(
                "project",
                "active",
                RunId,
                "DUT-42",
                BatchId: null,
                FixtureId: null,
                DeviceId: null,
                "actor-a"),
            "C:/automation",
            writer);

        Assert.Equal(RunnerExitCodes.ProductionRunExecutionFailed, exitCode);
        using var document = JsonDocument.Parse(writer.ToString());
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(
            "Failed",
            document.RootElement.GetProperty("productionRun").GetProperty("status").GetString());
        Assert.Equal(
            "Runtime.StageFailed",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static ProductionRunSnapshot CreateProductionRun(ProductionRunStatus status)
    {
        var createdAt = new DateTimeOffset(2026, 7, 10, 1, 0, 0, TimeSpan.Zero);
        var stageStatus = status == ProductionRunStatus.Completed
            ? ProductionStageRunStatus.Completed
            : ProductionStageRunStatus.Failed;
        var failureCode = status == ProductionRunStatus.Completed ? null : "Runtime.StageFailed";
        var failureReason = status == ProductionRunStatus.Completed ? null : "Stage failed.";
        var stage = new ProductionStageRunSnapshot(
            "stage.main",
            1,
            "workstation.main",
            new StationId("station.main"),
            new ProcessDefinitionId("process.main"),
            new ProcessVersionId("process.main@1.0.0"),
            new ConfigurationSnapshotId("configuration.main"),
            new RecipeSnapshotId("recipe.main@1.0.0"),
            stageStatus,
            new RuntimeSessionId(Guid.Parse("00000000-0000-0000-0000-000000000043")),
            createdAt.AddSeconds(1),
            createdAt.AddSeconds(2),
            failureCode,
            failureReason,
            CompletedStepCount: status == ProductionRunStatus.Completed ? 3 : 1,
            CommandCount: status == ProductionRunStatus.Completed ? 3 : 2,
            IncidentCount: status == ProductionRunStatus.Completed ? 0 : 1);

        return new ProductionRunSnapshot(
            new ProductionRunId(RunId),
            "project.line-a",
            "application.main",
            "snapshot.active",
            "topology.main",
            "line.main",
            new DutIdentity("dut.main", "serialNumber", "DUT-42"),
            "batch-a",
            "fixture-a",
            "device-a",
            "actor-a",
            status,
            createdAt,
            createdAt.AddSeconds(2),
            createdAt.AddSeconds(1),
            createdAt.AddSeconds(2),
            failureCode,
            failureReason,
            [stage]);
    }

    private static RunnerCommand CreateCommand(
        AutomationProjectWorkspaceDetails workspace,
        IProjectReleaseProductionRunLauncher launcher)
    {
        return new RunnerCommand(
            new StubWorkspaceService(workspace),
            launcher,
            new StubProductionRunRecoveryService(),
            new StubTerminalOutboxDispatcher(),
            new StubExecutionCoordinator());
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
            snapshot.ProductionLineDefinitionId,
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

    private sealed class CapturingProductionRunLauncher(
        Result<ProductionRunRunResult> result)
        : IProjectReleaseProductionRunLauncher
    {
        public PublishedProjectSnapshotDetails? Snapshot { get; private set; }

        public StartProjectReleaseProductionRunRequest? Request { get; private set; }

        public ValueTask<Result<ProductionRunRunResult>> StartAsync(
            PublishedProjectSnapshotDetails snapshot,
            StartProjectReleaseProductionRunRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshot = snapshot;
            Request = request;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class StubProductionRunRecoveryService : IProductionRunRecoveryService
    {
        public ValueTask<ProductionRunRecoveryResult> RecoverAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ProductionRunRecoveryResult(0, 0, 0));
        }
    }

    private sealed class StubTerminalOutboxDispatcher : IProductionRunTerminalOutboxDispatcher
    {
        public ValueTask<int> DrainAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        }
    }

    private sealed class StubExecutionCoordinator : IProjectExecutionCoordinator
    {
        public ValueTask<IProjectExecutionLease?> TryAcquireAsync(
            string projectDirectory,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IProjectExecutionLease?>(new StubExecutionLease());
        }
    }

    private sealed class StubExecutionLease : IProjectExecutionLease
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
