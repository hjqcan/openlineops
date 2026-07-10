using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Operations;
using OpenLineOps.Runtime.Domain.Targets;

namespace OpenLineOps.Runtime.Domain.Commands;

public sealed class RuntimeCommand : Entity<RuntimeCommandId>
{
    private RuntimeCommand(
        RuntimeCommandId id,
        RuntimeStepId stepId,
        RuntimeCapabilityId targetCapability,
        string commandName,
        DateTimeOffset createdAtUtc,
        TimeSpan timeout,
        RuntimeActionId actionId,
        RuntimeTargetReference target)
        : base(id)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Command timeout must be greater than zero.");
        }

        StepId = stepId;
        ActionId = actionId ?? throw new ArgumentNullException(nameof(actionId));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        TargetCapability = targetCapability;
        CommandName = Required(commandName, nameof(commandName));
        CreatedAtUtc = createdAtUtc;
        Timeout = timeout;
        DeadlineAtUtc = createdAtUtc.Add(timeout);
        Status = RuntimeCommandStatus.Pending;
    }

    public RuntimeStepId StepId { get; }

    public RuntimeActionId ActionId { get; }

    public RuntimeTargetReference Target { get; }

    public string TargetKind => Target.Kind;

    public string TargetId => Target.TargetId;

    public RuntimeCapabilityId TargetCapability { get; }

    public string CommandName { get; }

    public RuntimeCommandStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset DeadlineAtUtc { get; }

    public TimeSpan Timeout { get; }

    public DateTimeOffset? AcceptedAtUtc { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? ResultPayload { get; private set; }

    public string? FailureReason { get; private set; }

    public RuntimeCommandSemanticOutcome? SemanticOutcome { get; private set; }

    public bool IsTerminal => Status is RuntimeCommandStatus.Completed
        or RuntimeCommandStatus.Failed
        or RuntimeCommandStatus.TimedOut
        or RuntimeCommandStatus.Canceled
        or RuntimeCommandStatus.Rejected;

    public static RuntimeCommand Create(
        RuntimeCommandId id,
        RuntimeStepId stepId,
        RuntimeCapabilityId targetCapability,
        string commandName,
        DateTimeOffset createdAtUtc,
        TimeSpan timeout,
        RuntimeActionId actionId,
        RuntimeTargetReference target)
    {
        return new RuntimeCommand(
            id,
            stepId,
            targetCapability,
            commandName,
            createdAtUtc,
            timeout,
            actionId,
            target);
    }

    public static RuntimeCommand Restore(
        RuntimeCommandId id,
        RuntimeStepId stepId,
        RuntimeCapabilityId targetCapability,
        string commandName,
        RuntimeCommandStatus status,
        DateTimeOffset createdAtUtc,
        TimeSpan timeout,
        DateTimeOffset? acceptedAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? completedAtUtc,
        string? resultPayload,
        string? failureReason,
        RuntimeCommandSemanticOutcome? semanticOutcome,
        RuntimeActionId actionId,
        RuntimeTargetReference target)
    {
        var command = new RuntimeCommand(
            id,
            stepId,
            targetCapability,
            commandName,
            createdAtUtc,
            timeout,
            actionId,
            target)
        {
            Status = status,
            AcceptedAtUtc = acceptedAtUtc,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc,
            ResultPayload = resultPayload,
            FailureReason = CanonicalOptional(failureReason),
            SemanticOutcome = semanticOutcome
        };

        ValidateSemanticOutcome(status, semanticOutcome);

        return command;
    }

    internal RuntimeOperationResult Accept(DateTimeOffset acceptedAtUtc)
    {
        return TransitionTo(RuntimeCommandStatus.Accepted, acceptedAtUtc);
    }

    internal RuntimeOperationResult Start(DateTimeOffset startedAtUtc)
    {
        return TransitionTo(RuntimeCommandStatus.InProgress, startedAtUtc);
    }

    internal RuntimeOperationResult Complete(
        string? resultPayload,
        DateTimeOffset completedAtUtc,
        RuntimeCommandSemanticOutcome? semanticOutcome = null)
    {
        ValidateSemanticOutcome(RuntimeCommandStatus.Completed, semanticOutcome);
        var result = TransitionTo(RuntimeCommandStatus.Completed, completedAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        ResultPayload = resultPayload;
        SemanticOutcome = semanticOutcome;
        return result;
    }

    internal RuntimeOperationResult Fail(
        string reason,
        DateTimeOffset failedAtUtc,
        string? resultPayload = null,
        RuntimeCommandSemanticOutcome? semanticOutcome = null)
    {
        ValidateSemanticOutcome(RuntimeCommandStatus.Failed, semanticOutcome);
        var canonicalReason = Required(reason, nameof(reason));
        var result = TransitionTo(RuntimeCommandStatus.Failed, failedAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        FailureReason = canonicalReason;
        ResultPayload = resultPayload;
        SemanticOutcome = semanticOutcome;
        return result;
    }

    internal RuntimeOperationResult TimeoutAt(DateTimeOffset timedOutAtUtc)
    {
        FailureReason = "Command timed out.";

        return TransitionTo(RuntimeCommandStatus.TimedOut, timedOutAtUtc);
    }

    internal RuntimeOperationResult Cancel(
        DateTimeOffset canceledAtUtc,
        string reason = "Command canceled.",
        string? resultPayload = null,
        RuntimeCommandSemanticOutcome? semanticOutcome = null)
    {
        ValidateSemanticOutcome(RuntimeCommandStatus.Canceled, semanticOutcome);
        var canonicalReason = Required(reason, nameof(reason));
        var result = TransitionTo(RuntimeCommandStatus.Canceled, canceledAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        FailureReason = canonicalReason;
        ResultPayload = resultPayload;
        SemanticOutcome = semanticOutcome;
        return result;
    }

    internal RuntimeOperationResult Reject(string reason, DateTimeOffset rejectedAtUtc)
    {
        FailureReason = Required(reason, nameof(reason));

        return TransitionTo(RuntimeCommandStatus.Rejected, rejectedAtUtc);
    }

    private RuntimeOperationResult TransitionTo(RuntimeCommandStatus target, DateTimeOffset utcNow)
    {
        if (Status == target)
        {
            return RuntimeOperationResult.Accepted();
        }

        if (!CanTransition(Status, target))
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.CommandTransitionRejected",
                $"Command {Id} cannot transition from {Status} to {target}.");
        }

        Status = target;

        if (target == RuntimeCommandStatus.Accepted)
        {
            AcceptedAtUtc = utcNow;
        }

        if (target == RuntimeCommandStatus.InProgress)
        {
            StartedAtUtc = utcNow;
        }

        if (IsTerminal)
        {
            CompletedAtUtc = utcNow;
        }

        return RuntimeOperationResult.Accepted();
    }

    private static bool CanTransition(RuntimeCommandStatus from, RuntimeCommandStatus to)
    {
        if (from is RuntimeCommandStatus.Completed
            or RuntimeCommandStatus.Failed
            or RuntimeCommandStatus.TimedOut
            or RuntimeCommandStatus.Canceled
            or RuntimeCommandStatus.Rejected)
        {
            return false;
        }

        return (from, to) switch
        {
            (RuntimeCommandStatus.Pending, RuntimeCommandStatus.Accepted) => true,
            (RuntimeCommandStatus.Pending, RuntimeCommandStatus.Rejected) => true,
            (RuntimeCommandStatus.Pending, RuntimeCommandStatus.Canceled) => true,
            (RuntimeCommandStatus.Accepted, RuntimeCommandStatus.Rejected) => true,
            (RuntimeCommandStatus.Accepted, RuntimeCommandStatus.InProgress) => true,
            (RuntimeCommandStatus.Accepted, RuntimeCommandStatus.Canceled) => true,
            (RuntimeCommandStatus.InProgress, RuntimeCommandStatus.Completed) => true,
            (RuntimeCommandStatus.InProgress, RuntimeCommandStatus.Failed) => true,
            (RuntimeCommandStatus.InProgress, RuntimeCommandStatus.TimedOut) => true,
            (RuntimeCommandStatus.InProgress, RuntimeCommandStatus.Canceled) => true,
            (RuntimeCommandStatus.InProgress, RuntimeCommandStatus.Rejected) => true,
            _ => false
        };
    }

    private static string? CanonicalOptional(string? value)
    {
        return value is null ? null : Required(value, nameof(value));
    }

    private static void ValidateSemanticOutcome(
        RuntimeCommandStatus status,
        RuntimeCommandSemanticOutcome? semanticOutcome)
    {
        if (semanticOutcome is null)
        {
            return;
        }

        var valid = (status, semanticOutcome) switch
        {
            (RuntimeCommandStatus.Completed, RuntimeCommandSemanticOutcome.Passed) => true,
            (RuntimeCommandStatus.Failed, RuntimeCommandSemanticOutcome.Failed) => true,
            (RuntimeCommandStatus.Canceled, RuntimeCommandSemanticOutcome.Aborted) => true,
            _ => false
        };

        if (!valid)
        {
            throw new ArgumentException(
                $"Semantic outcome {semanticOutcome} is invalid for command status {status}.",
                nameof(semanticOutcome));
        }
    }

    private static string Required(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be non-empty canonical text.",
                parameterName)
            : value;
    }
}
