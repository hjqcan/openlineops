using System.Text.Json;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Projects.Api.Integrations;
using OpenLineOps.Projects.Application.ProjectWorkspaces;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runner;

public sealed class RunnerCommand
{
    private static readonly TimeSpan ControlledStopTimeout = TimeSpan.FromSeconds(30);
    private readonly IAutomationProjectWorkspaceService _workspaceService;
    private readonly IRunnerProductionUnitPreparer _productionUnitPreparer;
    private readonly IProjectReleaseProductionRunLauncher _productionRunLauncher;
    private readonly IProductionRunCompletionWaiter _completionWaiter;
    private readonly IProductionRunCoordinator _productionRunCoordinator;
    private readonly IProductionRunRepository _productionRunRepository;
    private readonly IProductionRunTerminalOutboxDispatcher _terminalOutboxDispatcher;

    public RunnerCommand(
        IAutomationProjectWorkspaceService workspaceService,
        IRunnerProductionUnitPreparer productionUnitPreparer,
        IProjectReleaseProductionRunLauncher productionRunLauncher,
        IProductionRunCompletionWaiter completionWaiter,
        IProductionRunCoordinator productionRunCoordinator,
        IProductionRunRepository productionRunRepository,
        IProductionRunTerminalOutboxDispatcher terminalOutboxDispatcher)
    {
        _workspaceService = workspaceService;
        _productionUnitPreparer = productionUnitPreparer;
        _productionRunLauncher = productionRunLauncher;
        _completionWaiter = completionWaiter;
        _productionRunCoordinator = productionRunCoordinator;
        _productionRunRepository = productionRunRepository;
        _terminalOutboxDispatcher = terminalOutboxDispatcher;
    }

    public async Task<int> RunAsync(
        RunnerRunOptions options,
        string currentDirectory,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
        ArgumentNullException.ThrowIfNull(output);

        string projectTarget;
        try
        {
            projectTarget = RunnerProjectPathResolver.ResolveProjectTarget(
                options.ProjectTarget,
                currentDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidDataException
                                           or IOException
                                           or UnauthorizedAccessException)
        {
            return await WriteFailureAsync(
                RunnerExitCodes.ProjectOpenFailed,
                options.ProjectTarget,
                "Runner.ProjectTargetInvalid",
                exception.Message,
                output).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        OpenLineOps.Application.Abstractions.Results.Result<AutomationProjectWorkspaceDetails> openResult;
        try
        {
            openResult = await _workspaceService
                .OpenAsync(new OpenAutomationProjectWorkspaceRequest(projectTarget), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidDataException
                                           or JsonException
                                           or IOException
                                           or UnauthorizedAccessException)
        {
            return await WriteFailureAsync(
                RunnerExitCodes.ProjectOpenFailed,
                projectTarget,
                "Runner.ProjectOpenFailed",
                exception.Message,
                output).ConfigureAwait(false);
        }

        if (openResult.IsFailure)
        {
            return await WriteFailureAsync(
                RunnerExitCodes.ProjectOpenFailed,
                projectTarget,
                openResult.Error.Code,
                openResult.Error.Message,
                output).ConfigureAwait(false);
        }

        var workspace = openResult.Value;
        var selection = RunnerSnapshotSelector.Select(workspace.Project, options.Snapshot);
        if (!selection.IsSuccess)
        {
            return await WriteFailureAsync(
                RunnerExitCodes.SnapshotSelectionFailed,
                projectTarget,
                selection.ErrorCode!,
                selection.ErrorMessage!,
                output).ConfigureAwait(false);
        }

        var snapshot = selection.Snapshot!;
        var preparation = await _productionUnitPreparer
            .PrepareAsync(snapshot, options, cancellationToken)
            .ConfigureAwait(false);
        if (preparation.IsFailure)
        {
            return await WriteFailureAsync(
                IsMissingRelease(preparation.Error.Code)
                    ? RunnerExitCodes.ImmutableReleaseMissing
                    : RunnerExitCodes.ProductionRunStartRejected,
                projectTarget,
                preparation.Error.Code,
                preparation.Error.Message,
                output,
                workspace,
                snapshot).ConfigureAwait(false);
        }

        var submission = await _productionRunLauncher
            .SubmitAsync(
                snapshot,
                new SubmitProjectReleaseProductionRunRequest(
                    options.ProductionRunId,
                    options.ProductionUnitId,
                    options.ActorId),
                cancellationToken)
            .ConfigureAwait(false);
        if (submission.IsFailure)
        {
            return await WriteFailureAsync(
                IsMissingRelease(submission.Error.Code)
                    ? RunnerExitCodes.ImmutableReleaseMissing
                    : RunnerExitCodes.ProductionRunStartRejected,
                projectTarget,
                submission.Error.Code,
                submission.Error.Message,
                output,
                workspace,
                snapshot).ConfigureAwait(false);
        }

        Result<ProductionRunSnapshot> execution;
        try
        {
            execution = await _completionWaiter
                .WaitForTerminalAsync(submission.Value.RunId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await RequestControlledStopAsync(
                    options,
                    projectTarget,
                    output,
                    workspace,
                    snapshot,
                    submission.Value)
                .ConfigureAwait(false);
        }
        if (execution.IsFailure)
        {
            return await WriteFailureAsync(
                RunnerExitCodes.ProductionRunExecutionFailed,
                projectTarget,
                execution.Error.Code,
                execution.Error.Message,
                output,
                workspace,
                snapshot,
                submission.Value).ConfigureAwait(false);
        }

        return await WriteTerminalOutcomeAsync(
            projectTarget,
            output,
            workspace,
            snapshot,
            execution.Value).ConfigureAwait(false);
    }

    private async Task<int> RequestControlledStopAsync(
        RunnerRunOptions options,
        string projectTarget,
        TextWriter output,
        AutomationProjectWorkspaceDetails workspace,
        OpenLineOps.Projects.Application.Projects.PublishedProjectSnapshotDetails snapshot,
        ProductionRunSnapshot submittedRun)
    {
        using var timeout = new CancellationTokenSource(ControlledStopTimeout);
        Result<ProductionRunSnapshot> stopped;
        try
        {
            stopped = await _productionRunCoordinator
                .CommandAsync(
                    submittedRun.RunId,
                    new ProductionRunCommandRequest(
                        ProductionRunCommand.SafeStop,
                        options.ActorId,
                        "Runner cancellation requested a controlled Safe Stop."),
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            var durableRun = await ReadDurableRunAsync(submittedRun.RunId).ConfigureAwait(false);
            if (durableRun.IsFailure)
            {
                return await WriteFailureAsync(
                    RunnerExitCodes.ProductionRunExecutionFailed,
                    projectTarget,
                    durableRun.Error.Code,
                    durableRun.Error.Message,
                    output,
                    workspace,
                    snapshot).ConfigureAwait(false);
            }

            if (IsTerminal(durableRun.Value.ExecutionStatus))
            {
                return await WriteTerminalOutcomeAsync(
                    projectTarget,
                    output,
                    workspace,
                    snapshot,
                    durableRun.Value).ConfigureAwait(false);
            }

            return await WriteFailureAsync(
                RunnerExitCodes.ProductionRunExecutionFailed,
                projectTarget,
                "Runner.ControlledStopTimedOut",
                $"Production Run {submittedRun.RunId.Value:D} did not acknowledge Safe Stop within {ControlledStopTimeout.TotalSeconds:0} seconds.",
                output,
                workspace,
                snapshot,
                durableRun.Value).ConfigureAwait(false);
        }
        if (stopped.IsFailure)
        {
            var durableRun = await ReadDurableRunAsync(submittedRun.RunId).ConfigureAwait(false);
            if (durableRun.IsFailure)
            {
                return await WriteFailureAsync(
                    RunnerExitCodes.ProductionRunExecutionFailed,
                    projectTarget,
                    durableRun.Error.Code,
                    durableRun.Error.Message,
                    output,
                    workspace,
                    snapshot).ConfigureAwait(false);
            }

            if (IsTerminal(durableRun.Value.ExecutionStatus))
            {
                return await WriteTerminalOutcomeAsync(
                    projectTarget,
                    output,
                    workspace,
                    snapshot,
                    durableRun.Value).ConfigureAwait(false);
            }

            return await WriteFailureAsync(
                RunnerExitCodes.ProductionRunExecutionFailed,
                projectTarget,
                stopped.Error.Code,
                stopped.Error.Message,
                output,
                workspace,
                snapshot,
                durableRun.Value).ConfigureAwait(false);
        }

        var run = stopped.Value;
        return IsTerminal(run.ExecutionStatus)
            ? await WriteTerminalOutcomeAsync(
                projectTarget,
                output,
                workspace,
                snapshot,
                run).ConfigureAwait(false)
            : await WriteFailureAsync(
                RunnerExitCodes.ProductionRunExecutionFailed,
                projectTarget,
                run.FailureCode ?? "Runner.ControlledStopDidNotCancel",
                run.FailureReason
                    ?? $"Production Run {run.RunId.Value:D} did not reach a durable terminal state after Safe Stop.",
                output,
                workspace,
                snapshot,
                run).ConfigureAwait(false);
    }

    private async ValueTask<Result<ProductionRunSnapshot>> ReadDurableRunAsync(
        ProductionRunId runId)
    {
        var entry = await _productionRunRepository
            .GetByIdAsync(runId, CancellationToken.None)
            .ConfigureAwait(false);
        return entry is null
            ? Result.Failure<ProductionRunSnapshot>(ApplicationError.NotFound(
                "Runner.ProductionRunNotFound",
                $"Production Run {runId.Value:D} disappeared after it was accepted."))
            : Result.Success(entry.Run.ToSnapshot());
    }

    private async Task<int> WriteTerminalOutcomeAsync(
        string projectTarget,
        TextWriter output,
        AutomationProjectWorkspaceDetails workspace,
        OpenLineOps.Projects.Application.Projects.PublishedProjectSnapshotDetails snapshot,
        ProductionRunSnapshot productionRun)
    {
        await _terminalOutboxDispatcher.DrainAsync(CancellationToken.None).ConfigureAwait(false);
        if (productionRun.ExecutionStatus == ExecutionStatus.Canceled)
        {
            return await WriteFailureAsync(
                RunnerExitCodes.Canceled,
                projectTarget,
                "Runner.ProductionRunCanceled",
                $"Production Run {productionRun.RunId.Value:D} was canceled.",
                output,
                workspace,
                snapshot,
                productionRun).ConfigureAwait(false);
        }

        if (productionRun.ExecutionStatus != ExecutionStatus.Completed)
        {
            return await WriteFailureAsync(
                RunnerExitCodes.ProductionRunExecutionFailed,
                projectTarget,
                productionRun.FailureCode ?? "Runner.ProductionRunFailed",
                productionRun.FailureReason
                    ?? $"Production Run {productionRun.RunId.Value:D} ended with execution status "
                    + $"{productionRun.ExecutionStatus} and control state {productionRun.ControlState}.",
                output,
                workspace,
                snapshot,
                productionRun).ConfigureAwait(false);
        }

        await RunnerJsonOutputWriter
            .WriteAsync(
                output,
                RunnerJsonOutput.Succeeded(projectTarget, workspace, snapshot, productionRun))
            .ConfigureAwait(false);
        return RunnerExitCodes.Success;
    }

    private static bool IsTerminal(ExecutionStatus status) => status is
        ExecutionStatus.Completed
        or ExecutionStatus.Failed
        or ExecutionStatus.TimedOut
        or ExecutionStatus.Canceled
        or ExecutionStatus.Rejected;

    private static bool IsMissingRelease(string errorCode)
    {
        return string.Equals(
            errorCode,
            "NotFound.Projects.ProjectReleaseNotFound",
            StringComparison.Ordinal);
    }

    private static async Task<int> WriteFailureAsync(
        int exitCode,
        string target,
        string errorCode,
        string errorMessage,
        TextWriter output,
        AutomationProjectWorkspaceDetails? workspace = null,
        OpenLineOps.Projects.Application.Projects.PublishedProjectSnapshotDetails? snapshot = null,
        OpenLineOps.Runtime.Domain.Runs.ProductionRunSnapshot? productionRun = null)
    {
        await RunnerJsonOutputWriter
            .WriteAsync(
                output,
                RunnerJsonOutput.Failed(
                    exitCode,
                    target,
                    errorCode,
                    errorMessage,
                    workspace,
                    snapshot,
                    productionRun))
            .ConfigureAwait(false);

        return exitCode;
    }
}
