using System.Text.Json;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Nodes;
using OpenLineOps.Processes.Domain.Transitions;
using OpenLineOps.Processes.Domain.Validation;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Scripting;
using OpenLineOps.Runtime.Application.Sessions;
using OpenLineOps.Runtime.Domain.Sessions;
using ProcessDefinitionId = OpenLineOps.Processes.Domain.Identifiers.ProcessDefinitionId;
using RuntimeCapabilityId = OpenLineOps.Runtime.Domain.Identifiers.RuntimeCapabilityId;
using RuntimeConfigurationSnapshotId = OpenLineOps.Runtime.Domain.Identifiers.ConfigurationSnapshotId;
using RuntimeNodeId = OpenLineOps.Runtime.Domain.Identifiers.RuntimeNodeId;
using RuntimeProcessDefinitionId = OpenLineOps.Runtime.Domain.Identifiers.ProcessDefinitionId;
using RuntimeProcessVersionId = OpenLineOps.Runtime.Domain.Identifiers.ProcessVersionId;
using RuntimeRecipeSnapshotId = OpenLineOps.Runtime.Domain.Identifiers.RecipeSnapshotId;
using RuntimeStationId = OpenLineOps.Runtime.Domain.Identifiers.StationId;

namespace OpenLineOps.Processes.Application.Runtime;

public sealed class ProcessRuntimeSessionLauncher : IProcessRuntimeSessionLauncher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IProcessDefinitionRepository _definitionRepository;
    private readonly IRuntimeSessionRunner _sessionRunner;
    private readonly IRuntimeConfigurationSnapshotResolver _configurationSnapshotResolver;

    public ProcessRuntimeSessionLauncher(
        IProcessDefinitionRepository definitionRepository,
        IRuntimeSessionRunner sessionRunner,
        IRuntimeConfigurationSnapshotResolver configurationSnapshotResolver)
    {
        _definitionRepository = definitionRepository;
        _sessionRunner = sessionRunner;
        _configurationSnapshotResolver = configurationSnapshotResolver;
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

            var executableProcessResult = BuildExecutableProcess(definition);
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

    private static Result<ExecutableRuntimeProcess> BuildExecutableProcess(ProcessDefinition definition)
    {
        if (!definition.IsPublished)
        {
            return Result.Failure<ExecutableRuntimeProcess>(ApplicationError.Conflict(
                "Processes.DefinitionNotPublished",
                $"Process definition {definition.Id} must be published before a runtime session can start."));
        }

        var graphReport = ProcessGraphValidator.Validate(definition);
        if (!graphReport.IsValid)
        {
            return Result.Failure<ExecutableRuntimeProcess>(ApplicationError.Validation(
                "Processes.PublishedGraphInvalid",
                $"Published process definition {definition.Id} is not executable because graph validation failed."));
        }

        var runtimeBranchingError = ValidateRuntimeBranching(definition);
        if (runtimeBranchingError is not null)
        {
            return Result.Failure<ExecutableRuntimeProcess>(runtimeBranchingError);
        }

        var runtimeNodes = definition.Nodes
            .Where(node => node.Kind is ProcessNodeKind.Command or ProcessNodeKind.PythonScript)
            .Select(ToRuntimeNode)
            .ToArray();

        if (runtimeNodes.Length == 0)
        {
            return Result.Failure<ExecutableRuntimeProcess>(ApplicationError.Validation(
                "Processes.RuntimeBridgeNoExecutableCommands",
                $"Process definition {definition.Id} does not contain executable command nodes."));
        }

        var startNode = definition.Nodes.Single(node => node.Kind == ProcessNodeKind.Start);
        var routingNodes = definition.Nodes
            .Where(node => node.Kind is ProcessNodeKind.Start or ProcessNodeKind.Decision or ProcessNodeKind.Delay or ProcessNodeKind.End)
            .Select(ToRuntimeRoutingNode)
            .ToArray();
        var runtimeTransitions = definition.Transitions
            .Select(ToRuntimeTransition)
            .ToArray();

        return Result.Success(new ExecutableRuntimeProcess(
            new RuntimeProcessDefinitionId(definition.Id.Value),
            new RuntimeProcessVersionId(definition.VersionId.Value),
            runtimeNodes)
        {
            StartNodeId = new RuntimeNodeId(startNode.Id.Value),
            RoutingNodes = routingNodes,
            Transitions = runtimeTransitions
        });
    }

    private static ExecutableRuntimeNode ToRuntimeNode(ProcessNode node)
    {
        if (node.Kind == ProcessNodeKind.PythonScript)
        {
            return new ExecutableRuntimeNode(
                new RuntimeNodeId(node.Id.Value),
                node.DisplayName,
                new RuntimeCapabilityId(RuntimeScriptCommand.PythonCapability),
                RuntimeScriptCommand.PythonCommandName,
                node.ScriptTimeout!.Value,
                JsonSerializer.Serialize(
                    new RuntimeScriptCommandPayload(
                        node.ScriptLanguage!,
                        node.ScriptSourceCode!,
                        node.ScriptVersion,
                        node.InputPayload),
                    JsonOptions));
        }

        return new ExecutableRuntimeNode(
            new RuntimeNodeId(node.Id.Value),
            node.DisplayName,
            new RuntimeCapabilityId(node.RequiredCapability!.Value),
            node.CommandName!,
            node.CommandTimeout!.Value,
            node.InputPayload);
    }

    private static ApplicationError? ValidateRuntimeBranching(ProcessDefinition definition)
    {
        var nodesById = definition.Nodes.ToDictionary(node => node.Id);
        foreach (var transitionGroup in definition.Transitions.GroupBy(transition => transition.FromNodeId))
        {
            var outgoingTransitions = transitionGroup.ToArray();
            if (outgoingTransitions.Length <= 1)
            {
                continue;
            }

            if (!nodesById.TryGetValue(transitionGroup.Key, out var sourceNode)
                || sourceNode.Kind != ProcessNodeKind.Decision)
            {
                return ApplicationError.Conflict(
                    "Processes.RuntimeBridgeBranchingRequiresDecision",
                    $"Process node {transitionGroup.Key} has multiple outgoing transitions; branching must go through a Decision node.");
            }

            var duplicateLabel = outgoingTransitions
                .Select(transition => string.IsNullOrWhiteSpace(transition.Label)
                    ? "default"
                    : transition.Label.Trim())
                .GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1)
                ?.Key;
            if (duplicateLabel is not null)
            {
                return ApplicationError.Conflict(
                    "Processes.RuntimeBridgeDecisionLabelDuplicate",
                    $"Decision node {transitionGroup.Key} has duplicate outgoing transition label '{duplicateLabel}'.");
            }
        }

        return null;
    }

    private static ExecutableRuntimeRoutingNode ToRuntimeRoutingNode(ProcessNode node)
    {
        var kind = node.Kind switch
        {
            ProcessNodeKind.Start => ExecutableRuntimeRoutingNodeKind.Start,
            ProcessNodeKind.Decision => ExecutableRuntimeRoutingNodeKind.Decision,
            ProcessNodeKind.Delay => ExecutableRuntimeRoutingNodeKind.Delay,
            ProcessNodeKind.End => ExecutableRuntimeRoutingNodeKind.End,
            _ => throw new InvalidOperationException($"Process node {node.Id} is executable, not a routing node.")
        };

        return new ExecutableRuntimeRoutingNode(
            new RuntimeNodeId(node.Id.Value),
            node.DisplayName,
            kind);
    }

    private static ExecutableRuntimeTransition ToRuntimeTransition(ProcessTransition transition)
    {
        return new ExecutableRuntimeTransition(
            new RuntimeNodeId(transition.FromNodeId.Value),
            new RuntimeNodeId(transition.ToNodeId.Value),
            transition.Label,
            transition.LoopPolicy == ProcessTransitionLoopPolicy.Counted
                ? transition.MaxTraversals
                : null);
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
