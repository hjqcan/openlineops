using System.Text.Json;
using OpenLineOps.Projects.Api.Integrations;
using OpenLineOps.Projects.Application.ProjectWorkspaces;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Recovery;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runner;

public sealed class RunnerCommand
{
    private readonly IAutomationProjectWorkspaceService _workspaceService;
    private readonly IProjectReleaseProductionRunLauncher _productionRunLauncher;
    private readonly IProductionRunRunner _productionRunRunner;
    private readonly IProductionRunRecoveryService _recoveryService;
    private readonly IProductionRunTerminalOutboxDispatcher _terminalOutboxDispatcher;

    public RunnerCommand(
        IAutomationProjectWorkspaceService workspaceService,
        IProjectReleaseProductionRunLauncher productionRunLauncher,
        IProductionRunRunner productionRunRunner,
        IProductionRunRecoveryService recoveryService,
        IProductionRunTerminalOutboxDispatcher terminalOutboxDispatcher)
    {
        _workspaceService = workspaceService;
        _productionRunLauncher = productionRunLauncher;
        _productionRunRunner = productionRunRunner;
        _recoveryService = recoveryService;
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
        await _recoveryService.RecoverAsync(cancellationToken).ConfigureAwait(false);
        await _terminalOutboxDispatcher.DrainAsync(cancellationToken).ConfigureAwait(false);

        var submission = await _productionRunLauncher
            .SubmitAsync(
                snapshot,
                new SubmitProjectReleaseProductionRunRequest(
                    options.ProductionRunId,
                    options.ProductionUnitIdentityValue,
                    options.ActorId,
                    options.LotId,
                    options.CarrierId,
                    options.SlotId,
                    options.FixtureId,
                    options.DeviceId),
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

        var execution = await _productionRunRunner
            .ExecuteAsync(submission.Value.RunId, cancellationToken)
            .ConfigureAwait(false);
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

        var productionRun = execution.Value.Run;
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
