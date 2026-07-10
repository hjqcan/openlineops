using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Identifiers;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Scripting;
using OpenLineOps.Runtime.Application.Sessions;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Operations;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Steps;

namespace OpenLineOps.Runtime.Application.Execution;

public sealed class RuntimeSessionRunner : IRuntimeSessionRunner
{
    private static readonly StringComparer BranchLabelComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly TimeSpan MaximumDynamicContainerTimeout =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1d);

    private readonly IRuntimeSessionRepository _sessionRepository;
    private readonly IRuntimeDomainEventPublisher _domainEventPublisher;
    private readonly IRuntimeCommandExecutor _commandExecutor;
    private readonly IRuntimeIdProvider _idProvider;
    private readonly IClock _clock;
    private readonly RuntimeAutomationPlanExpander _automationPlanExpander;

    public RuntimeSessionRunner(
        IRuntimeSessionRepository sessionRepository,
        IRuntimeDomainEventPublisher domainEventPublisher,
        IRuntimeCommandExecutor commandExecutor,
        IRuntimeIdProvider idProvider,
        IClock clock,
        RuntimeAutomationPlanExpander? automationPlanExpander = null)
    {
        _sessionRepository = sessionRepository;
        _domainEventPublisher = domainEventPublisher;
        _commandExecutor = commandExecutor;
        _idProvider = idProvider;
        _clock = clock;
        _automationPlanExpander = automationPlanExpander ?? new RuntimeAutomationPlanExpander();
    }

    public async ValueTask<Result<RuntimeSessionRunResult>> RunAsync(
        StartRuntimeSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(request);
        if (validationError is not null)
        {
            return Result.Failure<RuntimeSessionRunResult>(validationError);
        }

        var session = RuntimeSession.Create(
            _idProvider.NewSessionId(),
            request.StationId,
            request.Process.ProcessDefinitionId,
            request.Process.ProcessVersionId,
            request.ConfigurationSnapshotId,
            request.RecipeSnapshotId,
            _clock.UtcNow,
            request.TraceMetadata);

        await PersistAndPublishAsync(session, cancellationToken).ConfigureAwait(false);

        var startResult = session.Start(_clock.UtcNow);
        if (!startResult.Succeeded)
        {
            return ToApplicationFailure(startResult);
        }

        await PersistAndPublishAsync(session, cancellationToken).ConfigureAwait(false);

        return request.Process.UsesGraph
            ? await RunGraphAsync(session, request.Process, cancellationToken).ConfigureAwait(false)
            : await RunLinearAsync(session, request.Process.Nodes, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<RuntimeSessionRunResult>> RunLinearAsync(
        RuntimeSession session,
        IReadOnlyList<ExecutableRuntimeNode> nodes,
        CancellationToken cancellationToken)
    {
        foreach (var node in nodes)
        {
            var nodeResult = await ExecuteNodeAsync(session, node, cancellationToken).ConfigureAwait(false);
            if (nodeResult.IsFailure)
            {
                return Result.Failure<RuntimeSessionRunResult>(nodeResult.Error);
            }

            if (session.IsTerminal)
            {
                return Result.Success(CreateRunResult(session));
            }
        }

        return await CompleteSessionAsync(session, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<RuntimeSessionRunResult>> RunGraphAsync(
        RuntimeSession session,
        ExecutableRuntimeProcess process,
        CancellationToken cancellationToken)
    {
        var executableNodes = process.Nodes.ToDictionary(node => node.NodeId);
        var routingNodes = process.RoutingNodes.ToDictionary(node => node.NodeId);
        var transitionsBySource = process.Transitions
            .GroupBy(transition => transition.FromNodeId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var transitionTraversals = new Dictionary<ExecutableRuntimeTransition, int>();
        var currentNodeId = process.StartNodeId!;
        var graphSize = process.Nodes.Count + process.RoutingNodes.Count + process.Transitions.Count;
        var loopTraversalBudget = process.Transitions.Sum(transition => transition.MaxTraversals.GetValueOrDefault());
        var hopLimit = Math.Max(
            1,
            (graphSize * (loopTraversalBudget + 1)) + 1);
        string? lastCommandPayload = null;

        for (var hop = 0; hop < hopLimit; hop++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (executableNodes.TryGetValue(currentNodeId, out var executableNode))
            {
                var nodeResult = await ExecuteNodeAsync(session, executableNode, cancellationToken)
                    .ConfigureAwait(false);
                if (nodeResult.IsFailure)
                {
                    return Result.Failure<RuntimeSessionRunResult>(nodeResult.Error);
                }

                lastCommandPayload = nodeResult.Value.ResultPayload;
                if (session.IsTerminal)
                {
                    return Result.Success(nodeResult.Value.RunResult);
                }

                var nextTransition = ResolveSingleOutgoingTransition(
                    executableNode.NodeId,
                    transitionsBySource,
                    "executable");
                if (nextTransition.IsFailure)
                {
                    return nextTransition.Error.Code == "Conflict.Runtime.ProcessGraphBranchingRequiresDecision"
                        ? await FailSessionAsync(
                            session,
                            "Runtime.ProcessGraphBranchingRequiresDecision",
                            nextTransition.Error.Message,
                            cancellationToken).ConfigureAwait(false)
                        : Result.Failure<RuntimeSessionRunResult>(nextTransition.Error);
                }

                if (nextTransition.Value is null)
                {
                    return await CompleteSessionAsync(session, cancellationToken).ConfigureAwait(false);
                }

                var traversalError = RecordTransitionTraversal(nextTransition.Value, transitionTraversals);
                if (traversalError is not null)
                {
                    return await FailSessionAsync(
                        session,
                        traversalError.Code.Replace("Conflict.", string.Empty, StringComparison.Ordinal),
                        traversalError.Message,
                        cancellationToken).ConfigureAwait(false);
                }

                currentNodeId = nextTransition.Value.ToNodeId;
                continue;
            }

            if (!routingNodes.TryGetValue(currentNodeId, out var routingNode))
            {
                return await FailSessionAsync(
                    session,
                    "Runtime.ProcessGraphNodeMissing",
                    $"Runtime process graph references missing node {currentNodeId}.",
                    cancellationToken).ConfigureAwait(false);
            }

            if (routingNode.Kind == ExecutableRuntimeRoutingNodeKind.End)
            {
                return await CompleteSessionAsync(session, cancellationToken).ConfigureAwait(false);
            }

            Result<ExecutableRuntimeTransition?> routeResult = routingNode.Kind == ExecutableRuntimeRoutingNodeKind.Decision
                ? ResolveDecisionTransition(routingNode, lastCommandPayload, transitionsBySource)
                : ResolveSingleOutgoingTransition(
                    routingNode.NodeId,
                    transitionsBySource,
                    routingNode.Kind.ToString());

            if (routeResult.IsFailure)
            {
                return await FailSessionAsync(
                    session,
                    routeResult.Error.Code.Replace("Conflict.", string.Empty, StringComparison.Ordinal),
                    routeResult.Error.Message,
                    cancellationToken).ConfigureAwait(false);
            }

            if (routeResult.Value is null)
            {
                return await CompleteSessionAsync(session, cancellationToken).ConfigureAwait(false);
            }

            var routeTraversalError = RecordTransitionTraversal(routeResult.Value, transitionTraversals);
            if (routeTraversalError is not null)
            {
                return await FailSessionAsync(
                    session,
                    routeTraversalError.Code.Replace("Conflict.", string.Empty, StringComparison.Ordinal),
                    routeTraversalError.Message,
                    cancellationToken).ConfigureAwait(false);
            }

            currentNodeId = routeResult.Value.ToNodeId;
        }

        return await FailSessionAsync(
            session,
            "Runtime.ProcessGraphHopLimitExceeded",
            $"Runtime process graph exceeded the hop limit of {hopLimit}.",
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<RuntimeSessionRunResult>> CompleteSessionAsync(
        RuntimeSession session,
        CancellationToken cancellationToken)
    {
        var completeResult = session.Complete(_clock.UtcNow);
        if (!completeResult.Succeeded)
        {
            return ToApplicationFailure(completeResult);
        }

        await PersistAndPublishAsync(session, cancellationToken).ConfigureAwait(false);

        return Result.Success(CreateRunResult(session));
    }

    private async ValueTask<Result<RuntimeNodeExecutionResult>> ExecuteNodeAsync(
        RuntimeSession session,
        ExecutableRuntimeNode node,
        CancellationToken cancellationToken)
    {
        var step = session.StartStep(
            _idProvider.NewStepId(),
            node.NodeId,
            node.DisplayName,
            _clock.UtcNow,
            node.EffectiveActionId);

        await PersistAndPublishAsync(session, cancellationToken).ConfigureAwait(false);

        var command = session.CreateCommand(
            _idProvider.NewCommandId(),
            step.Id,
            node.TargetCapability,
            node.CommandName,
            _clock.UtcNow,
            node.Timeout);

        var acceptResult = session.AcceptCommand(command.Id, _clock.UtcNow);
        if (!acceptResult.Succeeded)
        {
            return ToNodeExecutionFailure(acceptResult);
        }

        var startResult = session.StartCommand(command.Id, _clock.UtcNow);
        if (!startResult.Succeeded)
        {
            return ToNodeExecutionFailure(startResult);
        }

        await PersistAndPublishAsync(session, cancellationToken).ConfigureAwait(false);

        using var containerTimeoutCancellation = node.DynamicChildren is null
            ? null
            : new CancellationTokenSource(node.Timeout);
        using var containerLinkedCancellation = containerTimeoutCancellation is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                containerTimeoutCancellation.Token);
        var executionCancellationToken = containerLinkedCancellation?.Token ?? cancellationToken;

        var executionContext = new RuntimeCommandExecutionContext(
            session.Id,
            session.StationId,
            session.ConfigurationSnapshotId,
            step.Id,
            command.Id,
            node.NodeId,
            node.TargetCapability,
            node.CommandName,
            node.InputPayload,
            node.Timeout,
            session.TraceMetadata.ProjectId,
            session.TraceMetadata.ApplicationId,
            session.TraceMetadata.ProjectSnapshotId,
            step.ActionId,
            step.ParentStepId,
            step.DynamicSequence);

        var executionResult = await ExecuteCommandSafelyAsync(
                executionContext,
                executionCancellationToken)
            .ConfigureAwait(false);
        executionResult = NormalizeContainerCancellation(
            executionResult,
            containerTimeoutCancellation,
            cancellationToken);

        if (executionResult.Outcome == RuntimeCommandExecutionOutcome.Completed
            && node.DynamicChildren is not null)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return await CancelNodeAsync(
                    session,
                    step.Id,
                    command.Id,
                    "Automation plan execution was canceled.",
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (containerTimeoutCancellation!.IsCancellationRequested)
            {
                return await TimeoutNodeAsync(
                    session,
                    step.Id,
                    command.Id,
                    "Automation plan container timed out.",
                    cancellationToken).ConfigureAwait(false);
            }

            var expansionResult = _automationPlanExpander.Expand(node, executionResult.Payload);
            if (expansionResult.IsFailure)
            {
                return await FailNodeAsync(
                    session,
                    step.Id,
                    command.Id,
                    "Runtime.AutomationPlanInvalid",
                    expansionResult.Error.Message,
                    cancellationToken).ConfigureAwait(false);
            }

            var dispatchResults = new List<RuntimeAutomationPlanActionResult>();
            foreach (var action in expansionResult.Value.Actions)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return await CancelNodeAsync(
                        session,
                        step.Id,
                        command.Id,
                        "Automation plan execution was canceled.",
                        CancellationToken.None).ConfigureAwait(false);
                }

                if (containerTimeoutCancellation.IsCancellationRequested)
                {
                    return await TimeoutNodeAsync(
                        session,
                        step.Id,
                        command.Id,
                        "Automation plan container timed out.",
                        cancellationToken).ConfigureAwait(false);
                }

                Result<RuntimeAutomationPlanActionResult> childResult;
                try
                {
                    childResult = await ExecuteChildActionAsync(
                        session,
                        step.Id,
                        action,
                        new DynamicContainerCancellation(
                            containerTimeoutCancellation,
                            executionCancellationToken),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is ArgumentException
                                                   or InvalidOperationException)
                {
                    return await FailNodeAsync(
                        session,
                        step.Id,
                        command.Id,
                        "Runtime.ChildLifecycleFailed",
                        exception.Message,
                        cancellationToken.IsCancellationRequested
                            ? CancellationToken.None
                            : cancellationToken).ConfigureAwait(false);
                }

                if (childResult.IsFailure)
                {
                    return await FailNodeAsync(
                        session,
                        step.Id,
                        command.Id,
                        "Runtime.ChildLifecycleFailed",
                        childResult.Error.Message,
                        cancellationToken.IsCancellationRequested
                            ? CancellationToken.None
                            : cancellationToken).ConfigureAwait(false);
                }

                dispatchResults.Add(childResult.Value);
                if (childResult.Value.Outcome == RuntimeCommandExecutionOutcome.Completed)
                {
                    continue;
                }

                var childReason = childResult.Value.Reason
                    ?? $"Automation action {childResult.Value.ActionId} {childResult.Value.Outcome}.";
                if (containerTimeoutCancellation.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested)
                {
                    return await TimeoutNodeAsync(
                        session,
                        step.Id,
                        command.Id,
                        "Automation plan container timed out.",
                        cancellationToken).ConfigureAwait(false);
                }

                if (childResult.Value.Outcome == RuntimeCommandExecutionOutcome.Canceled)
                {
                    return await CancelNodeAsync(
                        session,
                        step.Id,
                        command.Id,
                        childReason,
                        CancellationToken.None).ConfigureAwait(false);
                }

                return await FailNodeAsync(
                    session,
                    step.Id,
                    command.Id,
                    ToChildFailureCode(childResult.Value.Outcome),
                    childReason,
                    cancellationToken.IsCancellationRequested
                        ? CancellationToken.None
                        : cancellationToken).ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return await CancelNodeAsync(
                    session,
                    step.Id,
                    command.Id,
                    "Automation plan execution was canceled.",
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (containerTimeoutCancellation.IsCancellationRequested)
            {
                return await TimeoutNodeAsync(
                    session,
                    step.Id,
                    command.Id,
                    "Automation plan container timed out.",
                    cancellationToken).ConfigureAwait(false);
            }

            executionResult = RuntimeCommandExecutionResult.Completed(
                expansionResult.Value.CreateCompletionPayload(dispatchResults));
        }

        return executionResult.Outcome switch
        {
            RuntimeCommandExecutionOutcome.Completed => await CompleteNodeAsync(
                session,
                step.Id,
                command.Id,
                executionResult.Payload,
                cancellationToken).ConfigureAwait(false),
            RuntimeCommandExecutionOutcome.Failed => await FailNodeAsync(
                session,
                step.Id,
                command.Id,
                "Runtime.CommandFailed",
                executionResult.Reason ?? "Command failed.",
                cancellationToken).ConfigureAwait(false),
            RuntimeCommandExecutionOutcome.Rejected => await RejectNodeAsync(
                session,
                step.Id,
                command.Id,
                executionResult.Reason ?? "Command rejected.",
                cancellationToken).ConfigureAwait(false),
            RuntimeCommandExecutionOutcome.TimedOut => await TimeoutNodeAsync(
                session,
                step.Id,
                command.Id,
                executionResult.Reason ?? "Command timed out.",
                cancellationToken).ConfigureAwait(false),
            RuntimeCommandExecutionOutcome.Canceled => await CancelNodeAsync(
                session,
                step.Id,
                command.Id,
                executionResult.Reason ?? "Command canceled.",
                cancellationToken.IsCancellationRequested
                    ? CancellationToken.None
                    : cancellationToken).ConfigureAwait(false),
            _ => Result.Failure<RuntimeNodeExecutionResult>(ApplicationError.Conflict(
                "Runtime.UnsupportedCommandOutcome",
                $"Unsupported command outcome: {executionResult.Outcome}."))
        };
    }

    private async ValueTask<Result<RuntimeAutomationPlanActionResult>> ExecuteChildActionAsync(
        RuntimeSession session,
        RuntimeStepId parentStepId,
        RuntimeAutomationPlanAction action,
        DynamicContainerCancellation containerCancellation,
        CancellationToken externalCancellationToken)
    {
        var node = action.Node;
        var step = session.StartStep(
            _idProvider.NewStepId(),
            node.NodeId,
            node.DisplayName,
            _clock.UtcNow,
            node.EffectiveActionId,
            parentStepId,
            action.Sequence);

        await PersistAndPublishAsync(session, externalCancellationToken).ConfigureAwait(false);

        var command = session.CreateCommand(
            _idProvider.NewCommandId(),
            step.Id,
            node.TargetCapability,
            node.CommandName,
            _clock.UtcNow,
            node.Timeout);

        var acceptResult = session.AcceptCommand(command.Id, _clock.UtcNow);
        if (!acceptResult.Succeeded)
        {
            return Result.Failure<RuntimeAutomationPlanActionResult>(ApplicationError.Conflict(
                acceptResult.Code,
                acceptResult.Message));
        }

        var startResult = session.StartCommand(command.Id, _clock.UtcNow);
        if (!startResult.Succeeded)
        {
            return Result.Failure<RuntimeAutomationPlanActionResult>(ApplicationError.Conflict(
                startResult.Code,
                startResult.Message));
        }

        await PersistAndPublishAsync(session, externalCancellationToken).ConfigureAwait(false);

        var context = new RuntimeCommandExecutionContext(
            session.Id,
            session.StationId,
            session.ConfigurationSnapshotId,
            step.Id,
            command.Id,
            node.NodeId,
            node.TargetCapability,
            node.CommandName,
            node.InputPayload,
            node.Timeout,
            session.TraceMetadata.ProjectId,
            session.TraceMetadata.ApplicationId,
            session.TraceMetadata.ProjectSnapshotId,
            step.ActionId,
            step.ParentStepId,
            step.DynamicSequence);

        var executionResult = await ExecuteCommandSafelyAsync(
                context,
                containerCancellation.ExecutionToken)
            .ConfigureAwait(false);
        executionResult = NormalizeContainerCancellation(
            executionResult,
            containerCancellation.TimeoutCancellation,
            externalCancellationToken);
        var transitionResult = executionResult.Outcome switch
        {
            RuntimeCommandExecutionOutcome.Completed => CompleteChildAction(
                session,
                step.Id,
                command.Id,
                executionResult.Payload),
            RuntimeCommandExecutionOutcome.Failed => FailChildAction(
                session,
                step.Id,
                command.Id,
                executionResult.Reason ?? "Command failed."),
            RuntimeCommandExecutionOutcome.Rejected => RejectChildAction(
                session,
                step.Id,
                command.Id,
                executionResult.Reason ?? "Command rejected."),
            RuntimeCommandExecutionOutcome.TimedOut => TimeoutChildAction(
                session,
                step.Id,
                command.Id,
                executionResult.Reason ?? "Command timed out."),
            RuntimeCommandExecutionOutcome.Canceled => CancelChildAction(
                session,
                step.Id,
                command.Id),
            _ => RuntimeOperationResult.Rejected(
                "Runtime.UnsupportedCommandOutcome",
                $"Unsupported command outcome: {executionResult.Outcome}.")
        };
        if (!transitionResult.Succeeded)
        {
            return Result.Failure<RuntimeAutomationPlanActionResult>(ApplicationError.Conflict(
                transitionResult.Code,
                transitionResult.Message));
        }

        await PersistAndPublishAsync(
                session,
                externalCancellationToken.IsCancellationRequested
                    ? CancellationToken.None
                    : externalCancellationToken)
            .ConfigureAwait(false);

        return Result.Success(new RuntimeAutomationPlanActionResult(
            action.Sequence,
            step.ActionId.Value,
            step.NodeId.Value,
            action.ActionType,
            executionResult.Outcome,
            executionResult.Payload,
            executionResult.Reason));
    }

    private RuntimeOperationResult CompleteChildAction(
        RuntimeSession session,
        RuntimeStepId stepId,
        RuntimeCommandId commandId,
        string? payload)
    {
        var commandResult = session.CompleteCommand(commandId, payload, _clock.UtcNow);
        return commandResult.Succeeded
            ? session.CompleteStep(stepId, _clock.UtcNow)
            : commandResult;
    }

    private RuntimeOperationResult FailChildAction(
        RuntimeSession session,
        RuntimeStepId stepId,
        RuntimeCommandId commandId,
        string reason)
    {
        var commandResult = session.FailCommand(commandId, reason, _clock.UtcNow);
        return commandResult.Succeeded
            ? session.FailStep(stepId, reason, _clock.UtcNow)
            : commandResult;
    }

    private RuntimeOperationResult RejectChildAction(
        RuntimeSession session,
        RuntimeStepId stepId,
        RuntimeCommandId commandId,
        string reason)
    {
        var commandResult = session.RejectCommand(commandId, reason, _clock.UtcNow);
        return commandResult.Succeeded
            ? session.FailStep(stepId, reason, _clock.UtcNow)
            : commandResult;
    }

    private RuntimeOperationResult TimeoutChildAction(
        RuntimeSession session,
        RuntimeStepId stepId,
        RuntimeCommandId commandId,
        string reason)
    {
        var commandResult = session.TimeoutCommand(commandId, _clock.UtcNow);
        return commandResult.Succeeded
            ? session.FailStep(stepId, reason, _clock.UtcNow)
            : commandResult;
    }

    private RuntimeOperationResult CancelChildAction(
        RuntimeSession session,
        RuntimeStepId stepId,
        RuntimeCommandId commandId)
    {
        var commandResult = session.CancelCommand(commandId, _clock.UtcNow);
        return commandResult.Succeeded
            ? session.CancelStep(stepId, _clock.UtcNow)
            : commandResult;
    }

    private async ValueTask<RuntimeCommandExecutionResult> ExecuteCommandSafelyAsync(
        RuntimeCommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _commandExecutor.ExecuteAsync(context, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RuntimeCommandExecutionResult.Canceled("Command execution was canceled.");
        }
    }

    private static RuntimeCommandExecutionResult NormalizeContainerCancellation(
        RuntimeCommandExecutionResult result,
        CancellationTokenSource? containerTimeoutCancellation,
        CancellationToken externalCancellationToken)
    {
        if (result.Outcome != RuntimeCommandExecutionOutcome.Canceled
            || externalCancellationToken.IsCancellationRequested
            || containerTimeoutCancellation is null
            || !containerTimeoutCancellation.IsCancellationRequested)
        {
            return result;
        }

        return RuntimeCommandExecutionResult.TimedOut(
            "Automation plan container timed out.");
    }

    private static string ToChildFailureCode(RuntimeCommandExecutionOutcome outcome)
    {
        return outcome switch
        {
            RuntimeCommandExecutionOutcome.Failed => "Runtime.ChildCommandFailed",
            RuntimeCommandExecutionOutcome.Rejected => "Runtime.ChildCommandRejected",
            RuntimeCommandExecutionOutcome.TimedOut => "Runtime.ChildCommandTimedOut",
            _ => "Runtime.ChildCommandFailed"
        };
    }

    private async ValueTask<Result<RuntimeNodeExecutionResult>> CompleteNodeAsync(
        RuntimeSession session,
        RuntimeStepId stepId,
        RuntimeCommandId commandId,
        string? payload,
        CancellationToken cancellationToken)
    {
        var commandResult = session.CompleteCommand(commandId, payload, _clock.UtcNow);
        if (!commandResult.Succeeded)
        {
            return ToNodeExecutionFailure(commandResult);
        }

        var stepResult = session.CompleteStep(stepId, _clock.UtcNow);
        if (!stepResult.Succeeded)
        {
            return ToNodeExecutionFailure(stepResult);
        }

        await PersistAndPublishAsync(session, cancellationToken).ConfigureAwait(false);

        return Result.Success(new RuntimeNodeExecutionResult(CreateRunResult(session), payload));
    }

    private async ValueTask<Result<RuntimeNodeExecutionResult>> FailNodeAsync(
        RuntimeSession session,
        RuntimeStepId stepId,
        RuntimeCommandId commandId,
        string code,
        string reason,
        CancellationToken cancellationToken)
    {
        var commandResult = session.FailCommand(commandId, reason, _clock.UtcNow);
        if (!commandResult.Succeeded)
        {
            return ToNodeExecutionFailure(commandResult);
        }

        return await FailStepAndSessionAsync(
            session,
            stepId,
            code,
            reason,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<RuntimeNodeExecutionResult>> RejectNodeAsync(
        RuntimeSession session,
        RuntimeStepId stepId,
        RuntimeCommandId commandId,
        string reason,
        CancellationToken cancellationToken)
    {
        var commandResult = session.RejectCommand(commandId, reason, _clock.UtcNow);
        if (!commandResult.Succeeded)
        {
            return ToNodeExecutionFailure(commandResult);
        }

        return await FailStepAndSessionAsync(
            session,
            stepId,
            "Runtime.CommandRejected",
            reason,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<RuntimeNodeExecutionResult>> TimeoutNodeAsync(
        RuntimeSession session,
        RuntimeStepId stepId,
        RuntimeCommandId commandId,
        string reason,
        CancellationToken cancellationToken)
    {
        var commandResult = session.TimeoutCommand(commandId, _clock.UtcNow);
        if (!commandResult.Succeeded)
        {
            return ToNodeExecutionFailure(commandResult);
        }

        return await FailStepAndSessionAsync(
            session,
            stepId,
            "Runtime.CommandTimedOut",
            reason,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<RuntimeNodeExecutionResult>> CancelNodeAsync(
        RuntimeSession session,
        RuntimeStepId stepId,
        RuntimeCommandId commandId,
        string reason,
        CancellationToken cancellationToken)
    {
        var commandResult = session.CancelCommand(commandId, _clock.UtcNow);
        if (!commandResult.Succeeded)
        {
            return ToNodeExecutionFailure(commandResult);
        }

        var stepResult = session.CancelStep(stepId, _clock.UtcNow);
        if (!stepResult.Succeeded)
        {
            return ToNodeExecutionFailure(stepResult);
        }

        var sessionResult = session.Cancel(_clock.UtcNow, reason);
        if (!sessionResult.Succeeded)
        {
            return ToNodeExecutionFailure(sessionResult);
        }

        await PersistAndPublishAsync(session, cancellationToken).ConfigureAwait(false);

        return Result.Success(new RuntimeNodeExecutionResult(CreateRunResult(session), null));
    }

    private async ValueTask<Result<RuntimeNodeExecutionResult>> FailStepAndSessionAsync(
        RuntimeSession session,
        RuntimeStepId stepId,
        string code,
        string reason,
        CancellationToken cancellationToken)
    {
        var stepResult = session.FailStep(stepId, reason, _clock.UtcNow);
        if (!stepResult.Succeeded)
        {
            return ToNodeExecutionFailure(stepResult);
        }

        var sessionResult = session.Fail(_clock.UtcNow, code, reason);
        if (!sessionResult.Succeeded)
        {
            return ToNodeExecutionFailure(sessionResult);
        }

        await PersistAndPublishAsync(session, cancellationToken).ConfigureAwait(false);

        return Result.Success(new RuntimeNodeExecutionResult(CreateRunResult(session), null));
    }

    private async ValueTask<Result<RuntimeSessionRunResult>> FailSessionAsync(
        RuntimeSession session,
        string code,
        string reason,
        CancellationToken cancellationToken)
    {
        var sessionResult = session.Fail(_clock.UtcNow, code, reason);
        if (!sessionResult.Succeeded)
        {
            return ToApplicationFailure(sessionResult);
        }

        await PersistAndPublishAsync(session, cancellationToken).ConfigureAwait(false);

        return Result.Success(CreateRunResult(session));
    }

    private async ValueTask PersistAndPublishAsync(
        RuntimeSession session,
        CancellationToken cancellationToken)
    {
        var domainEvents = session.DomainEvents.ToArray();

        await _sessionRepository.SaveAsync(session, cancellationToken).ConfigureAwait(false);

        if (domainEvents.Length > 0)
        {
            await _domainEventPublisher.PublishAsync(domainEvents, cancellationToken).ConfigureAwait(false);
            session.ClearDomainEvents();
        }
    }

    private static ApplicationError? Validate(StartRuntimeSessionRequest request)
    {
        if (request.Process.Nodes.Count == 0)
        {
            return ApplicationError.Validation(
                "Runtime.ProcessHasNoNodes",
                "A runtime session cannot start from an empty process.");
        }

        var invalidNode = request.Process.Nodes.FirstOrDefault(node => !node.IsValid);
        if (invalidNode is not null)
        {
            return ApplicationError.Validation(
                "Runtime.ProcessNodeInvalid",
                $"Runtime node {invalidNode.NodeId} has invalid execution metadata.");
        }

        var unsupportedDynamicTimeout = request.Process.Nodes.FirstOrDefault(node =>
            node.DynamicChildren is not null
            && node.Timeout > MaximumDynamicContainerTimeout);
        if (unsupportedDynamicTimeout is not null)
        {
            return ApplicationError.Validation(
                "Runtime.DynamicContainerTimeoutUnsupported",
                $"Runtime node {unsupportedDynamicTimeout.NodeId} dynamic container timeout exceeds the supported timer range.");
        }

        if (request.Process.UsesGraph)
        {
            return ValidateGraph(request.Process);
        }

        return null;
    }

    private static ApplicationError? ValidateGraph(ExecutableRuntimeProcess process)
    {
        if (process.StartNodeId is null)
        {
            return ApplicationError.Validation(
                "Runtime.ProcessGraphStartMissing",
                "A runtime process graph must declare a start node id.");
        }

        var executableNodeIds = process.Nodes.Select(node => node.NodeId).ToArray();
        var routingNodeIds = process.RoutingNodes.Select(node => node.NodeId).ToArray();
        var allNodeIds = executableNodeIds.Concat(routingNodeIds).ToArray();
        if (allNodeIds.Distinct().Count() != allNodeIds.Length)
        {
            return ApplicationError.Validation(
                "Runtime.ProcessGraphDuplicateNode",
                "A runtime process graph cannot contain duplicate node ids.");
        }

        if (!allNodeIds.Contains(process.StartNodeId))
        {
            return ApplicationError.Validation(
                "Runtime.ProcessGraphStartMissing",
                $"Runtime process graph start node {process.StartNodeId} is missing.");
        }

        var invalidRoutingNode = process.RoutingNodes.FirstOrDefault(node => !node.IsValid);
        if (invalidRoutingNode is not null)
        {
            return ApplicationError.Validation(
                "Runtime.ProcessGraphRoutingNodeInvalid",
                $"Runtime routing node {invalidRoutingNode.NodeId} has invalid metadata.");
        }

        var nodeIdSet = allNodeIds.ToHashSet();
        foreach (var transition in process.Transitions)
        {
            if (transition.MaxTraversals is not null and <= 0)
            {
                return ApplicationError.Validation(
                    "Runtime.ProcessGraphLoopTransitionLimitInvalid",
                    $"Runtime transition from {transition.FromNodeId} to {transition.ToNodeId} must declare a positive max traversal count.");
            }

            if (!nodeIdSet.Contains(transition.FromNodeId))
            {
                return ApplicationError.Validation(
                    "Runtime.ProcessGraphTransitionSourceMissing",
                    $"Runtime transition from {transition.FromNodeId} references a missing source node.");
            }

            if (!nodeIdSet.Contains(transition.ToNodeId))
            {
                return ApplicationError.Validation(
                    "Runtime.ProcessGraphTransitionTargetMissing",
                    $"Runtime transition to {transition.ToNodeId} references a missing target node.");
            }
        }

        var routingNodeKinds = process.RoutingNodes.ToDictionary(node => node.NodeId, node => node.Kind);
        var transitionsBySource = process.Transitions
            .GroupBy(transition => transition.FromNodeId)
            .ToDictionary(group => group.Key, group => group.ToArray());

        foreach (var (sourceNodeId, outgoingTransitions) in transitionsBySource)
        {
            if (outgoingTransitions.Length <= 1)
            {
                continue;
            }

            if (!routingNodeKinds.TryGetValue(sourceNodeId, out var sourceKind)
                || sourceKind != ExecutableRuntimeRoutingNodeKind.Decision)
            {
                return ApplicationError.Validation(
                    "Runtime.ProcessGraphBranchingRequiresDecision",
                    $"Runtime node {sourceNodeId} has multiple outgoing transitions but is not a decision node.");
            }
        }

        return null;
    }

    private static ApplicationError? RecordTransitionTraversal(
        ExecutableRuntimeTransition transition,
        Dictionary<ExecutableRuntimeTransition, int> transitionTraversals)
    {
        if (transition.MaxTraversals is null)
        {
            return null;
        }

        transitionTraversals.TryGetValue(transition, out var currentCount);
        if (currentCount >= transition.MaxTraversals.Value)
        {
            return ApplicationError.Conflict(
                "Runtime.LoopTransitionLimitExceeded",
                $"Runtime loop transition from {transition.FromNodeId} to {transition.ToNodeId} exceeded max traversals {transition.MaxTraversals.Value}.");
        }

        transitionTraversals[transition] = currentCount + 1;

        return null;
    }

    private static Result<ExecutableRuntimeTransition?> ResolveSingleOutgoingTransition(
        RuntimeNodeId nodeId,
        Dictionary<RuntimeNodeId, ExecutableRuntimeTransition[]> transitionsBySource,
        string nodeKind)
    {
        if (!transitionsBySource.TryGetValue(nodeId, out var transitions)
            || transitions.Length == 0)
        {
            return Result.Success<ExecutableRuntimeTransition?>(null);
        }

        if (transitions.Length == 1)
        {
            return Result.Success<ExecutableRuntimeTransition?>(transitions[0]);
        }

        return Result.Failure<ExecutableRuntimeTransition?>(ApplicationError.Conflict(
            "Runtime.ProcessGraphBranchingRequiresDecision",
            $"Runtime {nodeKind} node {nodeId} has multiple outgoing transitions; add a decision node for branching."));
    }

    private static Result<ExecutableRuntimeTransition?> ResolveDecisionTransition(
        ExecutableRuntimeRoutingNode decisionNode,
        string? lastCommandPayload,
        Dictionary<RuntimeNodeId, ExecutableRuntimeTransition[]> transitionsBySource)
    {
        if (!transitionsBySource.TryGetValue(decisionNode.NodeId, out var transitions)
            || transitions.Length == 0)
        {
            return Result.Failure<ExecutableRuntimeTransition?>(ApplicationError.Conflict(
                "Runtime.DecisionTransitionMissing",
                $"Decision node {decisionNode.NodeId} has no outgoing transitions."));
        }

        if (transitions.Length == 1)
        {
            return Result.Success<ExecutableRuntimeTransition?>(transitions[0]);
        }

        var branchValues = ExtractDecisionBranchValues(lastCommandPayload);
        foreach (var branchValue in branchValues)
        {
            var matched = transitions.FirstOrDefault(transition =>
                string.Equals(transition.Label, branchValue, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                return Result.Success<ExecutableRuntimeTransition?>(matched);
            }
        }

        var defaultTransition = transitions.FirstOrDefault(transition =>
            string.Equals(transition.Label, "default", StringComparison.OrdinalIgnoreCase))
            ?? transitions.FirstOrDefault(transition => string.IsNullOrWhiteSpace(transition.Label));
        if (defaultTransition is not null)
        {
            return Result.Success<ExecutableRuntimeTransition?>(defaultTransition);
        }

        var branchSummary = branchValues.Length == 0
            ? "no branch value"
            : string.Join(", ", branchValues);
        return Result.Failure<ExecutableRuntimeTransition?>(ApplicationError.Conflict(
            "Runtime.DecisionBranchNotMatched",
            $"Decision node {decisionNode.NodeId} could not match {branchSummary} to any outgoing transition label."));
    }

    private static string[] ExtractDecisionBranchValues(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        try
        {
            var root = JsonNode.Parse(payload);
            if (root is JsonObject jsonObject)
            {
                var values = new List<string>();
                AddStringProperty(values, jsonObject, "decision");
                AddStringProperty(values, jsonObject, "route");
                AddStringProperty(values, jsonObject, "branch");
                AddStringProperty(values, jsonObject, "next");
                AddStringProperty(values, jsonObject, "status");

                return values
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(BranchLabelComparer)
                    .ToArray();
            }

            if (root is JsonValue jsonValue
                && jsonValue.TryGetValue<string>(out var text)
                && !string.IsNullOrWhiteSpace(text))
            {
                return [text.Trim()];
            }
        }
        catch (JsonException)
        {
            return [payload.Trim()];
        }

        return [];
    }

    private static void AddStringProperty(
        List<string> values,
        JsonObject jsonObject,
        string propertyName)
    {
        if (!jsonObject.TryGetPropertyValue(propertyName, out var node)
            || node is not JsonValue value
            || !value.TryGetValue<string>(out var text)
            || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        values.Add(text.Trim());
    }

    private static Result<RuntimeSessionRunResult> ToApplicationFailure(RuntimeOperationResult result)
    {
        return Result.Failure<RuntimeSessionRunResult>(
            ApplicationError.Conflict(result.Code, result.Message));
    }

    private static Result<RuntimeNodeExecutionResult> ToNodeExecutionFailure(RuntimeOperationResult result)
    {
        return Result.Failure<RuntimeNodeExecutionResult>(
            ApplicationError.Conflict(result.Code, result.Message));
    }

    private static RuntimeSessionRunResult CreateRunResult(RuntimeSession session)
    {
        return new RuntimeSessionRunResult(
            session.Id,
            session.ConfigurationSnapshotId,
            session.Status,
            session.Steps.Count(step => step.Status == RuntimeStepStatus.Completed),
            session.Commands.Count,
            session.Incidents.Count);
    }

    private sealed record RuntimeNodeExecutionResult(
        RuntimeSessionRunResult RunResult,
        string? ResultPayload);

    private sealed record DynamicContainerCancellation(
        CancellationTokenSource? TimeoutCancellation,
        CancellationToken ExecutionToken);
}
