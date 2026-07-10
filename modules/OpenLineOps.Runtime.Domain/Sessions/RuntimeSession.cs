using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Incidents;
using OpenLineOps.Runtime.Domain.Operations;
using OpenLineOps.Runtime.Domain.Steps;
using OpenLineOps.Runtime.Domain.Targets;

namespace OpenLineOps.Runtime.Domain.Sessions;

public sealed class RuntimeSession : AggregateRoot<RuntimeSessionId>
{
    private readonly List<RuntimeStep> _steps = [];
    private readonly List<RuntimeCommand> _commands = [];
    private readonly List<RuntimeIncident> _incidents = [];

    private RuntimeSession(
        RuntimeSessionId id,
        StationId stationId,
        ProcessDefinitionId processDefinitionId,
        ProcessVersionId processVersionId,
        ConfigurationSnapshotId configurationSnapshotId,
        RecipeSnapshotId recipeSnapshotId,
        RuntimeSessionTraceMetadata traceMetadata,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        StationId = stationId;
        ProcessDefinitionId = processDefinitionId;
        ProcessVersionId = processVersionId;
        ConfigurationSnapshotId = configurationSnapshotId;
        RecipeSnapshotId = recipeSnapshotId;
        TraceMetadata = traceMetadata ?? throw new ArgumentNullException(nameof(traceMetadata));
        CreatedAtUtc = createdAtUtc;
        LastTransitionAtUtc = createdAtUtc;
        Status = RuntimeSessionStatus.Created;
    }

    public StationId StationId { get; }

    public ProcessDefinitionId ProcessDefinitionId { get; }

    public ProcessVersionId ProcessVersionId { get; }

    public ConfigurationSnapshotId ConfigurationSnapshotId { get; }

    public RecipeSnapshotId RecipeSnapshotId { get; }

    public RuntimeSessionTraceMetadata TraceMetadata { get; }

    public RuntimeSessionStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset LastTransitionAtUtc { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? PausedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public IReadOnlyCollection<RuntimeStep> Steps => _steps.AsReadOnly();

    public IReadOnlyCollection<RuntimeCommand> Commands => _commands.AsReadOnly();

    public IReadOnlyCollection<RuntimeIncident> Incidents => _incidents.AsReadOnly();

    public bool IsTerminal => IsTerminalStatus(Status);

    public static RuntimeSession Create(
        RuntimeSessionId id,
        StationId stationId,
        ProcessDefinitionId processDefinitionId,
        ProcessVersionId processVersionId,
        ConfigurationSnapshotId configurationSnapshotId,
        RecipeSnapshotId recipeSnapshotId,
        DateTimeOffset createdAtUtc,
        RuntimeSessionTraceMetadata traceMetadata)
    {
        var session = new RuntimeSession(
            id,
            stationId,
            processDefinitionId,
            processVersionId,
            configurationSnapshotId,
            recipeSnapshotId,
            traceMetadata,
            createdAtUtc);

        session.RaiseDomainEvent(new RuntimeSessionCreatedDomainEvent(id));

        return session;
    }

    public static RuntimeSession Restore(
        RuntimeSessionId id,
        StationId stationId,
        ProcessDefinitionId processDefinitionId,
        ProcessVersionId processVersionId,
        ConfigurationSnapshotId configurationSnapshotId,
        RecipeSnapshotId recipeSnapshotId,
        RuntimeSessionStatus status,
        DateTimeOffset createdAtUtc,
        DateTimeOffset lastTransitionAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? pausedAtUtc,
        DateTimeOffset? completedAtUtc,
        IEnumerable<RuntimeStep> steps,
        IEnumerable<RuntimeCommand> commands,
        IEnumerable<RuntimeIncident> incidents,
        RuntimeSessionTraceMetadata traceMetadata)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(incidents);

        var restoredSteps = steps.ToArray();
        var restoredCommands = commands.ToArray();
        var restoredIncidents = incidents.ToArray();
        var stepsById = restoredSteps.ToDictionary(step => step.Id);
        foreach (var command in restoredCommands)
        {
            if (!stepsById.TryGetValue(command.StepId, out var step))
            {
                throw new InvalidOperationException(
                    $"Runtime command {command.Id} references missing step {command.StepId}.");
            }

            if (command.ActionId != step.ActionId || command.Target != step.Target)
            {
                throw new InvalidOperationException(
                    $"Runtime command {command.Id} semantic identity differs from owning step {step.Id}.");
            }
        }

        var session = new RuntimeSession(
            id,
            stationId,
            processDefinitionId,
            processVersionId,
            configurationSnapshotId,
            recipeSnapshotId,
            traceMetadata,
            createdAtUtc)
        {
            Status = status,
            LastTransitionAtUtc = lastTransitionAtUtc,
            StartedAtUtc = startedAtUtc,
            PausedAtUtc = pausedAtUtc,
            CompletedAtUtc = completedAtUtc
        };

        session._steps.AddRange(restoredSteps);
        session._commands.AddRange(restoredCommands);
        session._incidents.AddRange(restoredIncidents);
        session.ClearDomainEvents();

        return session;
    }

    public RuntimeOperationResult Queue(DateTimeOffset queuedAtUtc)
    {
        return TransitionTo(RuntimeSessionStatus.Queued, queuedAtUtc, "Session queued.");
    }

    public RuntimeOperationResult Start(DateTimeOffset startedAtUtc)
    {
        return TransitionTo(RuntimeSessionStatus.Running, startedAtUtc, "Session started.");
    }

    public RuntimeOperationResult RequestPause(DateTimeOffset requestedAtUtc, string reason)
    {
        if (Status == RuntimeSessionStatus.Paused)
        {
            return RuntimeOperationResult.Accepted("Session is already paused.");
        }

        return TransitionTo(RuntimeSessionStatus.Pausing, requestedAtUtc, RequiredReason(reason));
    }

    public RuntimeOperationResult ConfirmPaused(DateTimeOffset pausedAtUtc, string reason)
    {
        return TransitionTo(RuntimeSessionStatus.Paused, pausedAtUtc, RequiredReason(reason));
    }

    public RuntimeOperationResult Resume(DateTimeOffset resumedAtUtc, string reason)
    {
        return TransitionTo(RuntimeSessionStatus.Running, resumedAtUtc, RequiredReason(reason));
    }

    public RuntimeOperationResult RequestStop(DateTimeOffset requestedAtUtc, string reason)
    {
        if (Status == RuntimeSessionStatus.Stopped)
        {
            return RuntimeOperationResult.Accepted("Session is already stopped.");
        }

        return TransitionTo(RuntimeSessionStatus.Stopping, requestedAtUtc, RequiredReason(reason));
    }

    public RuntimeOperationResult MarkStopped(DateTimeOffset stoppedAtUtc, string reason)
    {
        return TransitionTo(RuntimeSessionStatus.Stopped, stoppedAtUtc, RequiredReason(reason));
    }

    public RuntimeOperationResult Complete(DateTimeOffset completedAtUtc)
    {
        return TransitionTo(RuntimeSessionStatus.Completed, completedAtUtc, "Session completed.");
    }

    public RuntimeOperationResult Cancel(DateTimeOffset canceledAtUtc, string reason)
    {
        if (Status == RuntimeSessionStatus.Canceled)
        {
            return RuntimeOperationResult.Accepted("Session is already canceled.");
        }

        return TransitionTo(RuntimeSessionStatus.Canceled, canceledAtUtc, RequiredReason(reason));
    }

    public RuntimeOperationResult Fail(DateTimeOffset failedAtUtc, string code, string message)
    {
        var incident = RecordIncident(
            RuntimeIncidentSeverity.Error,
            code,
            message,
            failedAtUtc);

        var result = TransitionTo(RuntimeSessionStatus.Failed, failedAtUtc, incident.Message);

        return result;
    }

    public RuntimeStep StartStep(
        RuntimeStepId stepId,
        RuntimeNodeId nodeId,
        string displayName,
        DateTimeOffset startedAtUtc,
        RuntimeActionId actionId,
        RuntimeTargetReference target)
    {
        EnsureCanExecuteWork();

        var step = RuntimeStep.Start(
            stepId,
            nodeId,
            displayName,
            startedAtUtc,
            actionId,
            target);
        _steps.Add(step);

        RaiseDomainEvent(new RuntimeStepStatusChangedDomainEvent(
            Id,
            step.Id,
            RuntimeStepStatus.Pending,
            RuntimeStepStatus.Running));

        return step;
    }

    public RuntimeOperationResult CompleteStep(RuntimeStepId stepId, DateTimeOffset completedAtUtc)
    {
        return ChangeStepStatus(stepId, step => step.Complete(completedAtUtc));
    }

    public RuntimeOperationResult FailStep(RuntimeStepId stepId, string reason, DateTimeOffset failedAtUtc)
    {
        return ChangeStepStatus(stepId, step => step.Fail(reason, failedAtUtc));
    }

    public RuntimeOperationResult CancelStep(RuntimeStepId stepId, DateTimeOffset canceledAtUtc)
    {
        return ChangeStepStatus(stepId, step => step.Cancel(canceledAtUtc));
    }

    public RuntimeCommand CreateCommand(
        RuntimeCommandId commandId,
        RuntimeStepId stepId,
        RuntimeCapabilityId targetCapability,
        string commandName,
        DateTimeOffset createdAtUtc,
        TimeSpan timeout)
    {
        EnsureCanExecuteWork();

        if (_steps.All(step => step.Id != stepId))
        {
            throw new InvalidOperationException($"Runtime step {stepId} does not exist in session {Id}.");
        }

        var command = RuntimeCommand.Create(
            commandId,
            stepId,
            targetCapability,
            commandName,
            createdAtUtc,
            timeout,
            _steps.Single(step => step.Id == stepId).ActionId,
            _steps.Single(step => step.Id == stepId).Target);

        _commands.Add(command);

        return command;
    }

    public RuntimeOperationResult AcceptCommand(RuntimeCommandId commandId, DateTimeOffset acceptedAtUtc)
    {
        return ChangeCommandStatus(commandId, command => command.Accept(acceptedAtUtc), "Command accepted.");
    }

    public RuntimeOperationResult StartCommand(RuntimeCommandId commandId, DateTimeOffset startedAtUtc)
    {
        return ChangeCommandStatus(commandId, command => command.Start(startedAtUtc), "Command started.");
    }

    public RuntimeOperationResult CompleteCommand(
        RuntimeCommandId commandId,
        string? resultPayload,
        DateTimeOffset completedAtUtc,
        RuntimeCommandSemanticOutcome? semanticOutcome = null)
    {
        return ChangeCommandStatus(
            commandId,
            command => command.Complete(resultPayload, completedAtUtc, semanticOutcome),
            "Command completed.");
    }

    public RuntimeOperationResult FailCommand(
        RuntimeCommandId commandId,
        string reason,
        DateTimeOffset failedAtUtc,
        string? resultPayload = null,
        RuntimeCommandSemanticOutcome? semanticOutcome = null)
    {
        return ChangeCommandStatus(
            commandId,
            command => command.Fail(reason, failedAtUtc, resultPayload, semanticOutcome),
            RequiredReason(reason));
    }

    public RuntimeOperationResult TimeoutCommand(RuntimeCommandId commandId, DateTimeOffset timedOutAtUtc)
    {
        return ChangeCommandStatus(commandId, command => command.TimeoutAt(timedOutAtUtc), "Command timed out.");
    }

    public RuntimeOperationResult CancelCommand(
        RuntimeCommandId commandId,
        DateTimeOffset canceledAtUtc,
        string reason = "Command canceled.",
        string? resultPayload = null,
        RuntimeCommandSemanticOutcome? semanticOutcome = null)
    {
        return ChangeCommandStatus(
            commandId,
            command => command.Cancel(canceledAtUtc, reason, resultPayload, semanticOutcome),
            RequiredReason(reason));
    }

    public RuntimeOperationResult RejectCommand(RuntimeCommandId commandId, string reason, DateTimeOffset rejectedAtUtc)
    {
        return ChangeCommandStatus(
            commandId,
            command => command.Reject(reason, rejectedAtUtc),
            RequiredReason(reason));
    }

    public RuntimeIncident RecordIncident(
        RuntimeIncidentSeverity severity,
        string code,
        string message,
        DateTimeOffset occurredAtUtc)
    {
        var incident = RuntimeIncident.Record(
            RuntimeIncidentId.New(),
            severity,
            code,
            message,
            occurredAtUtc);

        _incidents.Add(incident);

        RaiseDomainEvent(new RuntimeIncidentRecordedDomainEvent(
            Id,
            incident.Id,
            incident.Severity,
            incident.Code));

        return incident;
    }

    private RuntimeOperationResult ChangeStepStatus(
        RuntimeStepId stepId,
        Func<RuntimeStep, RuntimeOperationResult> transition)
    {
        var step = _steps.SingleOrDefault(candidate => candidate.Id == stepId);

        if (step is null)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.StepNotFound",
                $"Runtime step {stepId} was not found.");
        }

        var fromStatus = step.Status;
        var result = transition(step);

        if (result.Succeeded && fromStatus != step.Status)
        {
            RaiseDomainEvent(new RuntimeStepStatusChangedDomainEvent(Id, step.Id, fromStatus, step.Status));
        }

        return result;
    }

    private RuntimeOperationResult ChangeCommandStatus(
        RuntimeCommandId commandId,
        Func<RuntimeCommand, RuntimeOperationResult> transition,
        string reason)
    {
        var command = _commands.SingleOrDefault(candidate => candidate.Id == commandId);

        if (command is null)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.CommandNotFound",
                $"Runtime command {commandId} was not found.");
        }

        var fromStatus = command.Status;
        var result = transition(command);

        if (result.Succeeded && fromStatus != command.Status)
        {
            RaiseDomainEvent(new RuntimeCommandStatusChangedDomainEvent(
                Id,
                command.Id,
                fromStatus,
                command.Status,
                reason));
        }

        return result;
    }

    private RuntimeOperationResult TransitionTo(
        RuntimeSessionStatus target,
        DateTimeOffset utcNow,
        string reason)
    {
        if (Status == target)
        {
            return RuntimeOperationResult.Accepted();
        }

        if (!CanTransition(Status, target))
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.SessionTransitionRejected",
                $"Session {Id} cannot transition from {Status} to {target}.");
        }

        var fromStatus = Status;
        Status = target;
        LastTransitionAtUtc = utcNow;

        if (target == RuntimeSessionStatus.Running && StartedAtUtc is null)
        {
            StartedAtUtc = utcNow;
        }

        if (target == RuntimeSessionStatus.Paused)
        {
            PausedAtUtc = utcNow;
        }

        if (IsTerminalStatus(target))
        {
            CompletedAtUtc = utcNow;
        }

        RaiseDomainEvent(new RuntimeSessionStatusChangedDomainEvent(Id, fromStatus, target, reason));

        return RuntimeOperationResult.Accepted();
    }

    private void EnsureCanExecuteWork()
    {
        if (Status != RuntimeSessionStatus.Running)
        {
            throw new InvalidOperationException($"Runtime session {Id} must be running before work can be executed.");
        }
    }

    private static bool CanTransition(RuntimeSessionStatus from, RuntimeSessionStatus to)
    {
        if (IsTerminalStatus(from))
        {
            return false;
        }

        return (from, to) switch
        {
            (RuntimeSessionStatus.Created, RuntimeSessionStatus.Queued) => true,
            (RuntimeSessionStatus.Created, RuntimeSessionStatus.Running) => true,
            (RuntimeSessionStatus.Queued, RuntimeSessionStatus.Running) => true,
            (RuntimeSessionStatus.Running, RuntimeSessionStatus.Pausing) => true,
            (RuntimeSessionStatus.Running, RuntimeSessionStatus.Paused) => true,
            (RuntimeSessionStatus.Pausing, RuntimeSessionStatus.Paused) => true,
            (RuntimeSessionStatus.Paused, RuntimeSessionStatus.Running) => true,
            (RuntimeSessionStatus.Pausing, RuntimeSessionStatus.Running) => true,
            (RuntimeSessionStatus.Running, RuntimeSessionStatus.Stopping) => true,
            (RuntimeSessionStatus.Pausing, RuntimeSessionStatus.Stopping) => true,
            (RuntimeSessionStatus.Paused, RuntimeSessionStatus.Stopping) => true,
            (RuntimeSessionStatus.Stopping, RuntimeSessionStatus.Stopped) => true,
            (RuntimeSessionStatus.Running, RuntimeSessionStatus.Stopped) => true,
            (RuntimeSessionStatus.Pausing, RuntimeSessionStatus.Stopped) => true,
            (RuntimeSessionStatus.Paused, RuntimeSessionStatus.Stopped) => true,
            (RuntimeSessionStatus.Running, RuntimeSessionStatus.Completed) => true,
            (_, RuntimeSessionStatus.Canceled) => true,
            (_, RuntimeSessionStatus.Failed) => true,
            _ => false
        };
    }

    private static bool IsTerminalStatus(RuntimeSessionStatus status)
    {
        return status is RuntimeSessionStatus.Stopped
            or RuntimeSessionStatus.Completed
            or RuntimeSessionStatus.Failed
            or RuntimeSessionStatus.Canceled;
    }

    private static string RequiredReason(string reason)
    {
        return string.IsNullOrWhiteSpace(reason)
            || char.IsWhiteSpace(reason[0])
            || char.IsWhiteSpace(reason[^1])
            ? throw new ArgumentException(
                "Runtime transition reason must be non-empty canonical text.",
                nameof(reason))
            : reason;
    }
}
