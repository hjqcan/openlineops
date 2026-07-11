using System.Text.Json;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Projects.Api.Integrations;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.ProjectWorkspaces;
using OpenLineOps.Runner;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Recovery;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runner.Tests;

public sealed class RunnerCommandTests
{
    private static readonly Guid RunId =
        Guid.Parse("00000000-0000-0000-0000-000000000042");

    [Fact]
    public async Task RunSubmitsActiveImmutableGraphAndReportsDoubleAxisResult()
    {
        var snapshot = RunnerSnapshotSelectorTests.CreateSnapshot("snapshot.active");
        var workspace = CreateWorkspace(
            RunnerSnapshotSelectorTests.CreateProject("snapshot.active", [snapshot]),
            snapshot);
        var submitted = CreateProductionRun(ExecutionStatus.Pending, ResultJudgement.Unknown);
        var completed = CreateProductionRun(ExecutionStatus.Completed, ResultJudgement.Passed);
        var launcher = new CapturingProductionRunLauncher(Result.Success(submitted));
        var runner = new StubProductionRunRunner(Result.Success(new ProductionRunRunResult(completed)));
        var command = CreateCommand(workspace, launcher, runner);
        var writer = new StringWriter();

        var exitCode = await command.RunAsync(
            Options(),
            "C:/automation",
            writer);

        Assert.Equal(RunnerExitCodes.Success, exitCode);
        Assert.Same(snapshot, launcher.Snapshot);
        Assert.NotNull(launcher.Request);
        Assert.Equal(RunId, launcher.Request.ProductionRunId);
        Assert.Equal("UNIT-42", launcher.Request.ProductionUnitIdentityValue);
        Assert.Equal("lot-a", launcher.Request.LotId);
        Assert.Equal("carrier-a", launcher.Request.CarrierId);
        Assert.Equal("slot-a", launcher.Request.SlotId);
        Assert.Equal("fixture-a", launcher.Request.FixtureId);
        Assert.Equal("device-a", launcher.Request.DeviceId);
        Assert.Equal("actor-a", launcher.Request.ActorId);
        Assert.Equal(new ProductionRunId(RunId), runner.RunId);

        using var document = JsonDocument.Parse(writer.ToString());
        var root = document.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.False(root.TryGetProperty("schemaVersion", out _));
        Assert.Equal("snapshot.active", root.GetProperty("project").GetProperty("snapshotId").GetString());
        var productionRun = root.GetProperty("productionRun");
        Assert.Equal("Completed", productionRun.GetProperty("executionStatus").GetString());
        Assert.Equal("Passed", productionRun.GetProperty("resultJudgement").GetString());
        Assert.Equal("UNIT-42", productionRun.GetProperty("productionUnitIdentityValue").GetString());
        Assert.Equal(1, productionRun.GetProperty("operationCount").GetInt32());
        Assert.Equal(
            "operation.main",
            productionRun.GetProperty("operations")[0].GetProperty("operationId").GetString());
        Assert.False(root.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task ProductFailureIsCompletedExecutionAndReturnsSuccess()
    {
        var snapshot = RunnerSnapshotSelectorTests.CreateSnapshot("snapshot.active");
        var workspace = CreateWorkspace(
            RunnerSnapshotSelectorTests.CreateProject("snapshot.active", [snapshot]),
            snapshot);
        var completedNonconforming = CreateProductionRun(
            ExecutionStatus.Completed,
            ResultJudgement.Failed);
        var command = CreateCommand(
            workspace,
            new CapturingProductionRunLauncher(Result.Success(completedNonconforming)),
            new StubProductionRunRunner(Result.Success(
                new ProductionRunRunResult(completedNonconforming))));
        var writer = new StringWriter();

        var exitCode = await command.RunAsync(Options(), "C:/automation", writer);

        Assert.Equal(RunnerExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(writer.ToString());
        Assert.Equal(
            "Completed",
            document.RootElement.GetProperty("productionRun").GetProperty("executionStatus").GetString());
        Assert.Equal(
            "Failed",
            document.RootElement.GetProperty("productionRun").GetProperty("resultJudgement").GetString());
    }

    [Fact]
    public async Task SystemFailureReturnsProductionRunFailureExitCode()
    {
        var snapshot = RunnerSnapshotSelectorTests.CreateSnapshot("snapshot.active");
        var workspace = CreateWorkspace(
            RunnerSnapshotSelectorTests.CreateProject("snapshot.active", [snapshot]),
            snapshot);
        var failed = CreateProductionRun(ExecutionStatus.Failed, ResultJudgement.Unknown);
        var command = CreateCommand(
            workspace,
            new CapturingProductionRunLauncher(Result.Success(failed)),
            new StubProductionRunRunner(Result.Success(new ProductionRunRunResult(failed))));
        var writer = new StringWriter();

        var exitCode = await command.RunAsync(Options(), "C:/automation", writer);

        Assert.Equal(RunnerExitCodes.ProductionRunExecutionFailed, exitCode);
        using var document = JsonDocument.Parse(writer.ToString());
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(
            "Failed",
            document.RootElement.GetProperty("productionRun").GetProperty("executionStatus").GetString());
        Assert.Equal(
            "Runtime.OperationFailed",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static RunnerRunOptions Options() => new(
        "project",
        "active",
        RunId,
        "UNIT-42",
        "lot-a",
        "carrier-a",
        "slot-a",
        "fixture-a",
        "device-a",
        "actor-a");

    private static ProductionRunSnapshot CreateProductionRun(
        ExecutionStatus executionStatus,
        ResultJudgement judgement)
    {
        var createdAt = new DateTimeOffset(2026, 7, 10, 1, 0, 0, TimeSpan.Zero);
        var definition = new OperationRunDefinition(
            "operation.main",
            "station.main",
            new StationId("station.main"),
            new ProcessDefinitionId("process.main"),
            new ProcessVersionId("process.main@1.0.0"),
            new ConfigurationSnapshotId("configuration.main"),
            new RecipeSnapshotId("recipe.main@1.0.0"),
            [
                new ResourceRequirement(ResourceKind.Station, "station.main"),
                new ResourceRequirement(ResourceKind.Slot, "slot-a")
            ]);
        var terminal = executionStatus is ExecutionStatus.Completed
            or ExecutionStatus.Failed
            or ExecutionStatus.TimedOut
            or ExecutionStatus.Canceled
            or ExecutionStatus.Rejected;
        var failureCode = executionStatus == ExecutionStatus.Failed
            ? "Runtime.OperationFailed"
            : null;
        var failureReason = executionStatus == ExecutionStatus.Failed
            ? "Station operation failed."
            : null;
        var operation = new OperationRunSnapshot(
            definition,
            "operation.main#1",
            Attempt: 1,
            executionStatus,
            judgement,
            terminal ? new RuntimeSessionId(Guid.Parse("00000000-0000-0000-0000-000000000043")) : null,
            terminal ? createdAt.AddSeconds(1) : null,
            terminal ? createdAt.AddSeconds(2) : null,
            failureCode,
            failureReason,
            CompletedStepCount: executionStatus == ExecutionStatus.Completed ? 3 : 1,
            CommandCount: executionStatus == ExecutionStatus.Completed ? 3 : 2,
            IncidentCount: executionStatus == ExecutionStatus.Failed ? 1 : 0,
            Outputs: new Dictionary<string, ProductionContextValue>(StringComparer.Ordinal),
            FencingTokens: new Dictionary<ResourceRequirement, long>
            {
                [definition.ResourceRequirements[0]] = 10,
                [definition.ResourceRequirements[1]] = 11
            });

        return new ProductionRunSnapshot(
            new ProductionRunId(RunId),
            "project.line-a",
            "application.main",
            "snapshot.active",
            "topology.main",
            "line.main",
            new ProductionUnitIdentity("product.main", "serialNumber", "UNIT-42"),
            "lot-a",
            "carrier-a",
            "actor-a",
            executionStatus,
            judgement,
            judgement == ResultJudgement.Failed
                ? ProductDisposition.Nonconforming
                : executionStatus == ExecutionStatus.Completed
                    ? ProductDisposition.Completed
                    : ProductDisposition.Held,
            ProductionRunControlState.Active,
            createdAt,
            terminal ? createdAt.AddSeconds(2) : createdAt,
            terminal ? createdAt.AddSeconds(1) : null,
            terminal ? createdAt.AddSeconds(2) : null,
            failureCode,
            failureReason,
            "operation.main",
            [definition],
            [],
            [operation],
            [],
            new Dictionary<string, int>(StringComparer.Ordinal));
    }

    private static RunnerCommand CreateCommand(
        AutomationProjectWorkspaceDetails workspace,
        IProjectReleaseProductionRunLauncher launcher,
        IProductionRunRunner runner)
    {
        return new RunnerCommand(
            new StubWorkspaceService(workspace),
            launcher,
            runner,
            new StubProductionRunRecoveryService(),
            new StubTerminalOutboxDispatcher());
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
            CancellationToken cancellationToken = default) =>
            Task.FromException<Result<AutomationProjectWorkspaceDetails>>(
                new InvalidOperationException("Create is outside this test fixture."));

        public Task<Result<AutomationProjectWorkspaceDetails>> OpenAsync(
            OpenAutomationProjectWorkspaceRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result.Success(workspace));
        }

        public Task<Result<AutomationProjectWorkspaceDetails>> SaveManifestAsync(
            string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<Result<AutomationProjectWorkspaceDetails>>(
                new InvalidOperationException("Save is outside this test fixture."));

        public Task<Result<AutomationProjectWorkspaceDetails>> ImportApplicationAsync(
            string projectId,
            ImportAutomationProjectApplicationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<Result<AutomationProjectWorkspaceDetails>>(
                new InvalidOperationException("Import is outside this test fixture."));
    }

    private sealed class CapturingProductionRunLauncher(Result<ProductionRunSnapshot> result)
        : IProjectReleaseProductionRunLauncher
    {
        public PublishedProjectSnapshotDetails? Snapshot { get; private set; }

        public SubmitProjectReleaseProductionRunRequest? Request { get; private set; }

        public ValueTask<Result<ProductionRunSnapshot>> SubmitAsync(
            PublishedProjectSnapshotDetails snapshot,
            SubmitProjectReleaseProductionRunRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshot = snapshot;
            Request = request;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class StubProductionRunRunner(Result<ProductionRunRunResult> result)
        : IProductionRunRunner
    {
        public ProductionRunId? RunId { get; private set; }

        public ValueTask<Result<ProductionRunRunResult>> ExecuteAsync(
            ProductionRunId runId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunId = runId;
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
}
