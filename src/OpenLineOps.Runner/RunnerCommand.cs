using System.Text.Json;
using OpenLineOps.Processes.Application.Runtime;
using OpenLineOps.Projects.Api.Integrations;
using OpenLineOps.Projects.Application.ProjectWorkspaces;

namespace OpenLineOps.Runner;

public sealed class RunnerCommand
{
    private readonly IAutomationProjectWorkspaceService _workspaceService;
    private readonly IProjectReleaseRuntimeSessionLauncher _runtimeLauncher;

    public RunnerCommand(
        IAutomationProjectWorkspaceService workspaceService,
        IProjectReleaseRuntimeSessionLauncher runtimeLauncher)
    {
        _workspaceService = workspaceService;
        _runtimeLauncher = runtimeLauncher;
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
        var runResult = await _runtimeLauncher
            .StartAsync(
                snapshot,
                new StartProcessRuntimeSessionRequest(
                    snapshot.ConfigurationSnapshotId,
                    options.SerialNumber,
                    options.BatchId,
                    options.FixtureId,
                    options.DeviceId,
                    options.ActorId),
                cancellationToken)
            .ConfigureAwait(false);
        if (runResult.IsFailure)
        {
            return await WriteFailureAsync(
                RunnerExitCodes.RuntimeStartRejected,
                projectTarget,
                runResult.Error.Code,
                runResult.Error.Message,
                output,
                workspace,
                snapshot).ConfigureAwait(false);
        }

        var session = runResult.Value;
        if (!string.Equals(session.Status, "Completed", StringComparison.Ordinal))
        {
            return await WriteFailureAsync(
                RunnerExitCodes.RuntimeExecutionFailed,
                projectTarget,
                "Runner.RuntimeExecutionFailed",
                $"Runtime session {session.SessionId} ended with status {session.Status}.",
                output,
                workspace,
                snapshot,
                session).ConfigureAwait(false);
        }

        await RunnerJsonOutputWriter
            .WriteAsync(output, RunnerJsonOutput.Succeeded(projectTarget, workspace, snapshot, session))
            .ConfigureAwait(false);

        return RunnerExitCodes.Success;
    }

    private static async Task<int> WriteFailureAsync(
        int exitCode,
        string target,
        string errorCode,
        string errorMessage,
        TextWriter output,
        AutomationProjectWorkspaceDetails? workspace = null,
        OpenLineOps.Projects.Application.Projects.PublishedProjectSnapshotDetails? snapshot = null,
        StartedProcessRuntimeSessionDetails? session = null)
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
                    session))
            .ConfigureAwait(false);

        return exitCode;
    }
}
