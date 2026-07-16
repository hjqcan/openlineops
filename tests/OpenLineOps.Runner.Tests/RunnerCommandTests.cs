using System.Text.Json;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Projects.Api.Integrations;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.ProjectWorkspaces;
using OpenLineOps.Runner;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Persistence;
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
    public async Task CompletionWaiterReadsTheCoordinatorOwnedTerminalRun()
    {
        var completed = CreateProductionRun(ExecutionStatus.Completed, ResultJudgement.Passed);
        var waiter = new ProductionRunCompletionWaiter(
            new StubProductionRunRepository(completed));

        var result = await waiter.WaitForTerminalAsync(new ProductionRunId(RunId));

        Assert.True(result.IsSuccess);
        Assert.Equal(ExecutionStatus.Completed, result.Value.ExecutionStatus);
    }

    [Fact]
    public async Task CompletionWaiterFailsClosedWhenAcceptedRunDisappears()
    {
        var waiter = new ProductionRunCompletionWaiter(
            new StubProductionRunRepository(snapshot: null));

        var result = await waiter.WaitForTerminalAsync(new ProductionRunId(RunId));

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound.Runner.ProductionRunNotFound", result.Error.Code);
    }

    [Fact]
    public async Task CompletionWaiterReturnsDurableRecoveryRequiredOutcome()
    {
        var recoveryRequired = CreateRecoveryRequiredProductionRun();
        var waiter = new ProductionRunCompletionWaiter(
            new StubProductionRunRepository(recoveryRequired));

        var result = await waiter.WaitForTerminalAsync(new ProductionRunId(RunId));

        Assert.True(result.IsSuccess);
        Assert.Equal(ExecutionStatus.Running, result.Value.ExecutionStatus);
        Assert.Equal(ProductionRunControlState.RecoveryRequired, result.Value.ControlState);
        Assert.Equal("Runtime.RecoveryRequired", result.Value.FailureCode);
    }

    [Fact]
    public async Task CancellationRequestsSafeStopAndReturnsAfterDurableCanceledState()
    {
        var snapshot = RunnerSnapshotSelectorTests.CreateSnapshot("snapshot.active");
        var workspace = CreateWorkspace(
            RunnerSnapshotSelectorTests.CreateProject("snapshot.active", [snapshot]),
            snapshot);
        var submitted = CreateProductionRun(ExecutionStatus.Pending, ResultJudgement.Unknown);
        var canceled = CreateProductionRun(ExecutionStatus.Canceled, ResultJudgement.Aborted);
        using var cancellation = new CancellationTokenSource();
        var coordinator = new CapturingProductionRunCoordinator(Result.Success(canceled));
        var command = CreateCommand(
            workspace,
            new CapturingProductionRunLauncher(Result.Success(submitted)),
            new CancelingProductionRunCompletionWaiter(cancellation),
            coordinator);
        var writer = new StringWriter();

        var exitCode = await command.RunAsync(
            Options(),
            "C:/automation",
            writer,
            cancellation.Token);

        Assert.Equal(RunnerExitCodes.Canceled, exitCode);
        Assert.Equal(new ProductionRunId(RunId), coordinator.RunId);
        Assert.Equal(ProductionRunCommand.SafeStop, coordinator.Command?.Command);
        Assert.Equal("actor-a", coordinator.Command?.ActorId);
        using var document = JsonDocument.Parse(writer.ToString());
        Assert.Equal(
            "Canceled",
            document.RootElement.GetProperty("productionRun").GetProperty("executionStatus").GetString());
    }

    [Fact]
    public async Task ControlledStopRaceReportsDurableNaturalCompletion()
    {
        var snapshot = RunnerSnapshotSelectorTests.CreateSnapshot("snapshot.active");
        var workspace = CreateWorkspace(
            RunnerSnapshotSelectorTests.CreateProject("snapshot.active", [snapshot]),
            snapshot);
        var submitted = CreateProductionRun(ExecutionStatus.Pending, ResultJudgement.Unknown);
        var completed = CreateProductionRun(ExecutionStatus.Completed, ResultJudgement.Passed);
        using var cancellation = new CancellationTokenSource();
        var command = CreateCommand(
            workspace,
            new CapturingProductionRunLauncher(Result.Success(submitted)),
            new CancelingProductionRunCompletionWaiter(cancellation),
            new CapturingProductionRunCoordinator(
                Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    "Runtime.ProductionRunSafeStopRejected",
                    "The run completed while Safe Stop was being acknowledged."))),
            new StubProductionRunRepository(completed));
        var writer = new StringWriter();

        var exitCode = await command.RunAsync(
            Options(),
            "C:/automation",
            writer,
            cancellation.Token);

        Assert.Equal(RunnerExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(writer.ToString());
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(
            "Completed",
            document.RootElement.GetProperty("productionRun").GetProperty("executionStatus").GetString());
    }

    [Fact]
    public async Task ControlledStopFailsClosedWhenAcceptedRunDisappears()
    {
        var snapshot = RunnerSnapshotSelectorTests.CreateSnapshot("snapshot.active");
        var workspace = CreateWorkspace(
            RunnerSnapshotSelectorTests.CreateProject("snapshot.active", [snapshot]),
            snapshot);
        var submitted = CreateProductionRun(ExecutionStatus.Pending, ResultJudgement.Unknown);
        using var cancellation = new CancellationTokenSource();
        var command = CreateCommand(
            workspace,
            new CapturingProductionRunLauncher(Result.Success(submitted)),
            new CancelingProductionRunCompletionWaiter(cancellation),
            new CapturingProductionRunCoordinator(
                Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    "Runtime.ProductionRunSafeStopRejected",
                    "The accepted run could no longer be loaded."))),
            new StubProductionRunRepository(snapshot: null));
        var writer = new StringWriter();

        var exitCode = await command.RunAsync(
            Options(),
            "C:/automation",
            writer,
            cancellation.Token);

        Assert.Equal(RunnerExitCodes.ProductionRunExecutionFailed, exitCode);
        using var document = JsonDocument.Parse(writer.ToString());
        Assert.Equal(
            "NotFound.Runner.ProductionRunNotFound",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(document.RootElement.TryGetProperty("productionRun", out _));
    }

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
        var waiter = new StubProductionRunCompletionWaiter(Result.Success(completed));
        var command = CreateCommand(workspace, launcher, waiter);
        var writer = new StringWriter();

        var exitCode = await command.RunAsync(
            Options(),
            "C:/automation",
            writer);

        Assert.Equal(RunnerExitCodes.Success, exitCode);
        Assert.Same(snapshot, launcher.Snapshot);
        Assert.NotNull(launcher.Request);
        Assert.Equal(RunId, launcher.Request.ProductionRunId);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000043"), launcher.Request.ProductionUnitId);
        Assert.Equal("actor-a", launcher.Request.ActorId);
        Assert.Equal(new ProductionRunId(RunId), waiter.RunId);

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
            new StubProductionRunCompletionWaiter(Result.Success(completedNonconforming)));
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
            new StubProductionRunCompletionWaiter(Result.Success(failed)));
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

    [Fact]
    public async Task RecoveryRequiredReturnsProductionRunFailureWithoutWaitingForTerminalStatus()
    {
        var snapshot = RunnerSnapshotSelectorTests.CreateSnapshot("snapshot.active");
        var workspace = CreateWorkspace(
            RunnerSnapshotSelectorTests.CreateProject("snapshot.active", [snapshot]),
            snapshot);
        var recoveryRequired = CreateRecoveryRequiredProductionRun();
        var command = CreateCommand(
            workspace,
            new CapturingProductionRunLauncher(Result.Success(recoveryRequired)),
            new ProductionRunCompletionWaiter(
                new StubProductionRunRepository(recoveryRequired)));
        var writer = new StringWriter();

        var exitCode = await command.RunAsync(Options(), "C:/automation", writer);

        Assert.Equal(RunnerExitCodes.ProductionRunExecutionFailed, exitCode);
        using var document = JsonDocument.Parse(writer.ToString());
        Assert.Equal(
            "Runtime.RecoveryRequired",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
        var productionRun = document.RootElement.GetProperty("productionRun");
        Assert.Equal("Running", productionRun.GetProperty("executionStatus").GetString());
        Assert.Equal("RecoveryRequired", productionRun.GetProperty("controlState").GetString());
    }

    private static RunnerRunOptions Options() => new(
        "project",
        "active",
        RunId,
        Guid.Parse("00000000-0000-0000-0000-000000000043"),
        "UNIT-42",
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
                new ResourceRequirement(ResourceKind.Slot, "line-a/station-a/slot-a")
            ]);
        var terminal = executionStatus is ExecutionStatus.Completed
            or ExecutionStatus.Failed
            or ExecutionStatus.TimedOut
            or ExecutionStatus.Canceled
            or ExecutionStatus.Rejected;
        var failureCode = terminal && executionStatus != ExecutionStatus.Completed
            ? executionStatus == ExecutionStatus.Canceled
                ? "Runtime.OperationCanceled"
                : "Runtime.OperationFailed"
            : null;
        var failureReason = terminal && executionStatus != ExecutionStatus.Completed
            ? executionStatus == ExecutionStatus.Canceled
                ? "Station operation was canceled."
                : "Station operation failed."
            : null;
        var productionUnitId =
            OpenLineOps.Runtime.Domain.ProductionUnits.ProductionUnitId.New();
        const string operationRunId = "operation.main@0001";
        RuntimeSessionId? runtimeSessionId = terminal
            ? new RuntimeSessionId(Guid.Parse("00000000-0000-0000-0000-000000000043"))
            : null;
        DateTimeOffset? completedAtUtc = terminal ? createdAt.AddSeconds(2) : null;
        var executionEvidence = terminal
            ? new OperationExecutionEvidence(
                OperationExecutionEvidenceOrigin.Coordinator,
                runtimeSessionId!.Value.Value,
                RunId,
                productionUnitId.Value,
                "line.main",
                definition.OperationId,
                operationRunId,
                operationAttempt: 1,
                definition.StationSystemId,
                definition.StationId.Value,
                definition.ProcessDefinitionId.Value,
                definition.ProcessVersionId.Value,
                definition.ConfigurationSnapshotId.Value,
                definition.RecipeSnapshotId.Value,
                "product.main",
                "serialNumber",
                "UNIT-42",
                "lot-a",
                "carrier-a",
                fixtureId: null,
                deviceId: null,
                "actor-a",
                "project.line-a",
                "application.main",
                "snapshot.active",
                "topology.main",
                executionStatus switch
                {
                    ExecutionStatus.Completed => "Completed",
                    ExecutionStatus.Canceled => "Canceled",
                    _ => "Failed"
                },
                completedAtUtc!.Value,
                [
                    new OperationResourceFenceEvidence(
                        ResourceKind.Station.ToString(),
                        "station.main",
                        10,
                        completedAtUtc.Value.AddMinutes(1)),
                    new OperationResourceFenceEvidence(
                        ResourceKind.Slot.ToString(),
                        "line-a/station-a/slot-a",
                        11,
                        completedAtUtc.Value.AddMinutes(1))
                ],
                [],
                [],
                [],
                [])
            : null;
        var operation = new OperationRunSnapshot(
            definition,
            operationRunId,
            Attempt: 1,
            executionStatus,
            judgement,
            runtimeSessionId,
            terminal ? createdAt.AddSeconds(1) : null,
            completedAtUtc,
            failureCode,
            failureReason,
            CompletedStepCount: 0,
            CommandCount: 0,
            IncidentCount: 0,
            RecoveryDecisionId: null,
            executionEvidence,
            Outputs: new Dictionary<string, ProductionContextValue>(StringComparer.Ordinal),
            FencingTokens: executionStatus == ExecutionStatus.Pending
                ? new Dictionary<ResourceRequirement, long>()
                : new Dictionary<ResourceRequirement, long>
                {
                    [definition.ResourceRequirements[0]] = 10,
                    [definition.ResourceRequirements[1]] = 11
                },
            SourceOperationRunBindings: new Dictionary<string, string>(StringComparer.Ordinal));

        return new ProductionRunSnapshot(
            new ProductionRunId(RunId),
            "project.line-a",
            "application.main",
            "snapshot.active",
            "topology.main",
            "line.main",
            productionUnitId,
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
            SafeStopRequestedBy: null,
            SafeStopReason: null,
            SafeStopRequestedAtUtc: null,
            SafeStopAcknowledgedAtUtc: null,
            ScrapRequestedBy: null,
            ScrapReason: null,
            ScrapRequestedAtUtc: null,
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
            new Dictionary<string, int>(StringComparer.Ordinal),
            []);
    }

    private static ProductionRunSnapshot CreateRecoveryRequiredProductionRun()
    {
        var template = CreateProductionRun(ExecutionStatus.Pending, ResultJudgement.Unknown);
        var definition = Assert.Single(template.OperationDefinitions);
        var run = ProductionRun.Create(
            template.RunId,
            template.ProjectId,
            template.ApplicationId,
            template.ProjectSnapshotId,
            template.TopologyId,
            template.ProductionLineDefinitionId,
            template.ProductionUnitId,
            template.ProductionUnitIdentity,
            template.LotId,
            template.CarrierId,
            template.ActorId,
            template.EntryOperationId,
            template.CreatedAtUtc,
            [definition],
            [new RouteTransitionDefinition(
                "route.main.terminal",
                definition.OperationId,
                targetOperationId: null,
                RuntimeRouteTransitionKind.Sequence,
                terminalDisposition: ProductDisposition.Completed)]);
        var startedAtUtc = run.CreatedAtUtc.AddSeconds(1);
        Assert.True(run.Start(startedAtUtc).Succeeded);
        var operation = Assert.Single(run.Operations);
        var leases = operation.ResourceRequirements
            .Select((resource, index) => new ResourceLease(
                resource,
                run.Id,
                operation.OperationRunId,
                index + 1,
                startedAtUtc,
                startedAtUtc.AddMinutes(10)))
            .ToArray();
        Assert.True(run.StartOperation(
            operation.OperationRunId,
            new RuntimeSessionId(Guid.Parse("00000000-0000-0000-0000-000000000099")),
            leases,
            startedAtUtc.AddSeconds(1)).Succeeded);
        Assert.True(run.MarkRecoveryRequired(
            "The hardware command outcome is uncertain.",
            startedAtUtc.AddSeconds(2)).Succeeded);
        return run.ToSnapshot();
    }

    private static RunnerCommand CreateCommand(
        AutomationProjectWorkspaceDetails workspace,
        IProjectReleaseProductionRunLauncher launcher,
        IProductionRunCompletionWaiter completionWaiter,
        IProductionRunCoordinator? coordinator = null,
        IProductionRunRepository? repository = null)
    {
        return new RunnerCommand(
            new StubWorkspaceService(workspace),
            new StubProductionUnitPreparer(),
            launcher,
            completionWaiter,
            coordinator ?? new StubProductionRunCoordinator(),
            repository ?? new StubProductionRunRepository(snapshot: null),
            new StubTerminalOutboxDispatcher());
    }

    private sealed class StubProductionUnitPreparer : IRunnerProductionUnitPreparer
    {
        public ValueTask<Result<RunnerProductionUnitPreparation>> PrepareAsync(
            PublishedProjectSnapshotDetails snapshot,
            RunnerRunOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Result.Success(new RunnerProductionUnitPreparation(
                options.ProductionUnitId,
                "product.main",
                "serialNumber",
                options.ProductionUnitIdentityValue,
                "station.main")));
        }
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
                "applications/application.main/application.main.oloapp",
                PluginPackageReferences: [])],
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

    private sealed class StubProductionRunCompletionWaiter(Result<ProductionRunSnapshot> result)
        : IProductionRunCompletionWaiter
    {
        public ProductionRunId? RunId { get; private set; }

        public ValueTask<Result<ProductionRunSnapshot>> WaitForTerminalAsync(
            ProductionRunId runId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunId = runId;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CancelingProductionRunCompletionWaiter(
        CancellationTokenSource cancellation) : IProductionRunCompletionWaiter
    {
        public ValueTask<Result<ProductionRunSnapshot>> WaitForTerminalAsync(
            ProductionRunId runId,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromCanceled<Result<ProductionRunSnapshot>>(cancellationToken);
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

    private sealed class StubProductionRunCoordinator : IProductionRunCoordinator
    {
        public ValueTask<Result<ProductionRunSnapshot>> SubmitAsync(
            SubmitProductionRunRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                "Runner.TestUnexpectedSubmit",
                "RunnerCommand uses the immutable release launcher for submission.")));

        public ValueTask<Result<ProductionRunSnapshot>> CommandAsync(
            ProductionRunId runId,
            ProductionRunCommandRequest command,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                "Runner.TestUnexpectedCommand",
                "This test did not request cancellation.")));
    }

    private sealed class CapturingProductionRunCoordinator(Result<ProductionRunSnapshot> result)
        : IProductionRunCoordinator
    {
        public ProductionRunId? RunId { get; private set; }

        public ProductionRunCommandRequest? Command { get; private set; }

        public ValueTask<Result<ProductionRunSnapshot>> SubmitAsync(
            SubmitProductionRunRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                "Runner.TestUnexpectedSubmit",
                "RunnerCommand uses the immutable release launcher for submission.")));

        public ValueTask<Result<ProductionRunSnapshot>> CommandAsync(
            ProductionRunId runId,
            ProductionRunCommandRequest command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunId = runId;
            Command = command;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class StubProductionRunRepository(ProductionRunSnapshot? snapshot)
        : IProductionRunRepository
    {
        public ValueTask<ProductionRunPersistenceEntry?> GetByIdAsync(
            ProductionRunId runId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(new ProductionRunId(RunId), runId);
            return ValueTask.FromResult(snapshot is null
                ? null
                : new ProductionRunPersistenceEntry(ProductionRun.Restore(snapshot), revision: 1));
        }

        public ValueTask<bool> TryAddAsync(
            ProductionRun run,
            ProductionRunExecutionPlan executionPlan,
            ProductionRunAdmission admission,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

        public ValueTask<long> SaveAsync(
            ProductionRun run,
            long expectedRevision,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(0L);

        public ValueTask<IReadOnlyCollection<ProductionRunPersistenceEntry>> ListRecoverableAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult<
                IReadOnlyCollection<ProductionRunPersistenceEntry>>([]);

        public ValueTask<IReadOnlyCollection<ProductionRunPersistenceEntry>> ListActiveAsync(
            string? productionLineDefinitionId = null,
            string? stationSystemId = null,
            string? slotId = null,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<
                IReadOnlyCollection<ProductionRunPersistenceEntry>>([]);

        public ValueTask<ProductionRunTerminalPage> ListTerminalAsync(
            ProductionRunTerminalPageRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ProductionRunTerminalPage([], null));

        public ValueTask<IReadOnlyCollection<ProductionRunCreatedOutboxItem>>
            ListPendingCreatedOutboxAsync(
                int maximumCount,
                CancellationToken cancellationToken = default) => ValueTask.FromResult<
                    IReadOnlyCollection<ProductionRunCreatedOutboxItem>>([]);

        public ValueTask MarkCreatedOutboxProcessedAsync(
            ProductionRunId runId,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask RecordCreatedOutboxFailureAsync(
            ProductionRunId runId,
            string failureDescription,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyCollection<ProductionRunTerminalOutboxItem>> ListPendingTerminalOutboxAsync(
            int maximumCount,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<
                IReadOnlyCollection<ProductionRunTerminalOutboxItem>>([]);

        public ValueTask MarkTerminalOutboxProcessedAsync(
            ProductionRunId runId,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask RecordTerminalOutboxFailureAsync(
            ProductionRunId runId,
            string failureDescription,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
