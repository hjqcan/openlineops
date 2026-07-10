using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Runtime.Application.Sessions;
using OpenLineOps.Runtime.Domain.Sessions;
using ProcessDefinitionId = OpenLineOps.Processes.Domain.Identifiers.ProcessDefinitionId;
using RuntimeConfigurationSnapshotId = OpenLineOps.Runtime.Domain.Identifiers.ConfigurationSnapshotId;
using RuntimeRecipeSnapshotId = OpenLineOps.Runtime.Domain.Identifiers.RecipeSnapshotId;
using RuntimeStationId = OpenLineOps.Runtime.Domain.Identifiers.StationId;

namespace OpenLineOps.Processes.Application.Runtime;

public sealed class ProcessRuntimeSessionLauncher : IProcessRuntimeSessionLauncher
{
    private readonly IProcessDefinitionRepository _definitionRepository;
    private readonly IRuntimeSessionRunner _sessionRunner;
    private readonly IRuntimeConfigurationSnapshotResolver _configurationSnapshotResolver;
    private readonly IProcessFlowIrCompiler _flowIrCompiler;
    private readonly IFlowIrExecutableRuntimeProcessMapper _flowIrMapper;

    public ProcessRuntimeSessionLauncher(
        IProcessDefinitionRepository definitionRepository,
        IRuntimeSessionRunner sessionRunner,
        IRuntimeConfigurationSnapshotResolver configurationSnapshotResolver,
        IProcessFlowIrCompiler flowIrCompiler,
        IFlowIrExecutableRuntimeProcessMapper flowIrMapper)
    {
        _definitionRepository = definitionRepository;
        _sessionRunner = sessionRunner;
        _configurationSnapshotResolver = configurationSnapshotResolver;
        _flowIrCompiler = flowIrCompiler;
        _flowIrMapper = flowIrMapper;
    }

    public async ValueTask<Result<StartedProcessRuntimeSessionDetails>> StartAsync(
        string processDefinitionId,
        StartProcessRuntimeSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestValidation = ValidateRequest(processDefinitionId, request);
        if (requestValidation is not null)
        {
            return Result.Failure<StartedProcessRuntimeSessionDetails>(requestValidation);
        }

        try
        {
            var snapshotResult = await _configurationSnapshotResolver
                .ResolveAsync(request.ConfigurationSnapshotId, cancellationToken)
                .ConfigureAwait(false);

            if (snapshotResult.IsFailure)
            {
                return Result.Failure<StartedProcessRuntimeSessionDetails>(snapshotResult.Error);
            }

            var configurationSnapshot = snapshotResult.Value;
            if (!string.Equals(
                configurationSnapshot.ProcessDefinitionId,
                processDefinitionId,
                StringComparison.Ordinal))
            {
                return Result.Failure<StartedProcessRuntimeSessionDetails>(ApplicationError.Conflict(
                    "Processes.ConfigurationSnapshotProcessMismatch",
                    $"Configuration snapshot {configurationSnapshot.ConfigurationSnapshotId} belongs to process definition {configurationSnapshot.ProcessDefinitionId}, not {processDefinitionId}."));
            }

            var definition = await _definitionRepository
                .GetByIdAsync(new ProcessDefinitionId(processDefinitionId), cancellationToken)
                .ConfigureAwait(false);

            if (definition is null)
            {
                return Result.Failure<StartedProcessRuntimeSessionDetails>(ApplicationError.NotFound(
                    "Processes.DefinitionNotFound",
                    $"Process definition {processDefinitionId} was not found."));
            }

            if (!string.Equals(
                definition.VersionId.Value,
                configurationSnapshot.ProcessVersionId,
                StringComparison.Ordinal))
            {
                return Result.Failure<StartedProcessRuntimeSessionDetails>(ApplicationError.Conflict(
                    "Processes.ConfigurationSnapshotProcessVersionMismatch",
                    $"Configuration snapshot {configurationSnapshot.ConfigurationSnapshotId} references process version {configurationSnapshot.ProcessVersionId}, but the loaded definition is {definition.VersionId}."));
            }

            var compilationResult = _flowIrCompiler.Compile(definition);
            if (compilationResult.IsFailure)
            {
                return Result.Failure<StartedProcessRuntimeSessionDetails>(compilationResult.Error);
            }

            var executableProcessResult = _flowIrMapper.Map(compilationResult.Value.Document);
            if (executableProcessResult.IsFailure)
            {
                return Result.Failure<StartedProcessRuntimeSessionDetails>(executableProcessResult.Error);
            }

            var startRequest = new OpenLineOps.Runtime.Application.Sessions.StartRuntimeSessionRequest(
                new RuntimeStationId(configurationSnapshot.StationId),
                new RuntimeConfigurationSnapshotId(configurationSnapshot.ConfigurationSnapshotId),
                new RuntimeRecipeSnapshotId(configurationSnapshot.RecipeSnapshotId),
                executableProcessResult.Value,
                new RuntimeSessionTraceMetadata(
                    request.SerialNumber,
                    request.BatchId,
                    request.FixtureId,
                    request.DeviceId,
                    request.ActorId,
                    request.ProjectId,
                    request.ApplicationId,
                    request.ProjectSnapshotId,
                    request.TopologyId));

            var runResult = await _sessionRunner
                .RunAsync(startRequest, cancellationToken)
                .ConfigureAwait(false);

            return runResult.IsFailure
                ? Result.Failure<StartedProcessRuntimeSessionDetails>(runResult.Error)
                : Result.Success(ToDetails(runResult.Value));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<StartedProcessRuntimeSessionDetails>(ApplicationError.Validation(
                "Processes.RuntimeStartInputInvalid",
                exception.Message));
        }
    }

    private static StartedProcessRuntimeSessionDetails ToDetails(RuntimeSessionRunResult runResult)
    {
        return new StartedProcessRuntimeSessionDetails(
            runResult.SessionId.Value,
            runResult.ConfigurationSnapshotId.Value,
            runResult.Status.ToString(),
            runResult.CompletedSteps,
            runResult.CommandCount,
            runResult.IncidentCount);
    }

    private static ApplicationError? ValidateRequest(
        string processDefinitionId,
        StartProcessRuntimeSessionRequest request)
    {
        if (string.IsNullOrWhiteSpace(processDefinitionId))
        {
            return ApplicationError.Validation(
                "Processes.ProcessDefinitionIdRequired",
                "ProcessDefinitionId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ConfigurationSnapshotId))
        {
            return ApplicationError.Validation(
                "Processes.ConfigurationSnapshotIdRequired",
                "ConfigurationSnapshotId is required.");
        }

        return null;
    }
}
