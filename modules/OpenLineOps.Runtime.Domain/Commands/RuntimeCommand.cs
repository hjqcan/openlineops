using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Operations;

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
        RuntimeActionId? actionId)
        : base(id)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Command timeout must be greater than zero.");
        }

        StepId = stepId;
        ActionId = actionId ?? new RuntimeActionId($"legacy:command:{id.Value:D}");
        TargetCapability = targetCapability;
        CommandName = string.IsNullOrWhiteSpace(commandName)
            ? throw new ArgumentException("Command name cannot be empty.", nameof(commandName))
            : commandName.Trim();
        CreatedAtUtc = createdAtUtc;
        Timeout = timeout;
        DeadlineAtUtc = createdAtUtc.Add(timeout);
        Status = RuntimeCommandStatus.Pending;
    }

    public RuntimeStepId StepId { get; }

    public RuntimeActionId ActionId { get; }

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
        RuntimeActionId? actionId = null)
    {
        return new RuntimeCommand(
            id,
            stepId,
            targetCapability,
            commandName,
            createdAtUtc,
            timeout,
            actionId);
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
        RuntimeActionId? actionId = null)
    {
        var command = new RuntimeCommand(
            id,
            stepId,
            targetCapability,
            commandName,
            createdAtUtc,
            timeout,
            actionId)
        {
            Status = status,
            AcceptedAtUtc = acceptedAtUtc,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc,
            ResultPayload = NormalizeOptional(resultPayload),
            FailureReason = NormalizeOptional(failureReason)
        };

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

    internal RuntimeOperationResult Complete(string? resultPayload, DateTimeOffset completedAtUtc)
    {
        ResultPayload = string.IsNullOrWhiteSpace(resultPayload)
            ? null
            : resultPayload.Trim();

        return TransitionTo(RuntimeCommandStatus.Completed, completedAtUtc);
    }

    internal RuntimeOperationResult Fail(string reason, DateTimeOffset failedAtUtc)
    {
        FailureReason = string.IsNullOrWhiteSpace(reason)
            ? "Command failed."
            : reason.Trim();

        return TransitionTo(RuntimeCommandStatus.Failed, failedAtUtc);
    }

    internal RuntimeOperationResult TimeoutAt(DateTimeOffset timedOutAtUtc)
    {
        FailureReason = "Command timed out.";

        return TransitionTo(RuntimeCommandStatus.TimedOut, timedOutAtUtc);
    }

    internal RuntimeOperationResult Cancel(DateTimeOffset canceledAtUtc)
    {
        FailureReason = "Command canceled.";

        return TransitionTo(RuntimeCommandStatus.Canceled, canceledAtUtc);
    }

    internal RuntimeOperationResult Reject(string reason, DateTimeOffset rejectedAtUtc)
    {
        FailureReason = string.IsNullOrWhiteSpace(reason)
            ? "Command rejected."
            : reason.Trim();

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

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }
}
