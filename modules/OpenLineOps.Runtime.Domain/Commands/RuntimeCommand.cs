using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Operations;
using OpenLineOps.Runtime.Domain.Targets;
using CommandExecutionStatus = OpenLineOps.Runtime.Contracts.ExecutionStatus;
using CommandResultJudgement = OpenLineOps.Runtime.Contracts.ResultJudgement;

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
        Status = CommandExecutionStatus.Pending;
    }

    public RuntimeStepId StepId { get; }

    public RuntimeActionId ActionId { get; }

    public RuntimeTargetReference Target { get; }

    public string TargetKind => Target.Kind;

    public string TargetId => Target.TargetId;

    public RuntimeCapabilityId TargetCapability { get; }

    public string CommandName { get; }

    public CommandExecutionStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset DeadlineAtUtc { get; }

    public TimeSpan Timeout { get; }

    public DateTimeOffset? AcceptedAtUtc { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? ResultPayload { get; private set; }

    public string? FailureReason { get; private set; }

    public CommandResultJudgement? ResultJudgement { get; private set; }

    public bool IsTerminal => Status is CommandExecutionStatus.Completed
        or CommandExecutionStatus.Failed
        or CommandExecutionStatus.TimedOut
        or CommandExecutionStatus.Canceled
        or CommandExecutionStatus.Rejected;

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
        CommandExecutionStatus status,
        DateTimeOffset createdAtUtc,
        TimeSpan timeout,
        DateTimeOffset? acceptedAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? completedAtUtc,
        string? resultPayload,
        string? failureReason,
        CommandResultJudgement? resultJudgement,
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
            ResultJudgement = resultJudgement
        };

        ValidateRestoredLifecycle(command);

        return command;
    }

    internal RuntimeOperationResult Accept(DateTimeOffset acceptedAtUtc)
    {
        if (Status != CommandExecutionStatus.Pending)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.CommandTransitionRejected",
                $"Command {Id} cannot be accepted while execution status is {Status}.");
        }

        if (AcceptedAtUtc is not null)
        {
            return AcceptedAtUtc == acceptedAtUtc
                ? RuntimeOperationResult.Accepted()
                : RuntimeOperationResult.Rejected(
                    "Runtime.CommandEvidenceConflict",
                    $"Command {Id} was replayed with a different acceptance timestamp.");
        }

        AcceptedAtUtc = acceptedAtUtc;
        return RuntimeOperationResult.Accepted();
    }

    internal RuntimeOperationResult Start(DateTimeOffset startedAtUtc)
    {
        if (AcceptedAtUtc is null)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.CommandTransitionRejected",
                $"Command {Id} must be accepted before it can start running.");
        }

        return TransitionTo(CommandExecutionStatus.Running, startedAtUtc);
    }

    internal RuntimeOperationResult Complete(
        string? resultPayload,
        DateTimeOffset completedAtUtc,
        CommandResultJudgement resultJudgement = CommandResultJudgement.NotApplicable)
    {
        ValidateResultJudgement(CommandExecutionStatus.Completed, resultJudgement);
        if (Status == CommandExecutionStatus.Completed)
        {
            return ReplayTerminalEvidence(
                CommandExecutionStatus.Completed,
                completedAtUtc,
                resultPayload,
                null,
                resultJudgement);
        }

        var result = TransitionTo(CommandExecutionStatus.Completed, completedAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        ResultPayload = resultPayload;
        ResultJudgement = resultJudgement;
        return result;
    }

    internal RuntimeOperationResult Fail(
        string reason,
        DateTimeOffset failedAtUtc,
        string? resultPayload = null,
        CommandResultJudgement resultJudgement = CommandResultJudgement.Unknown)
    {
        ValidateResultJudgement(CommandExecutionStatus.Failed, resultJudgement);
        var canonicalReason = Required(reason, nameof(reason));
        if (Status == CommandExecutionStatus.Failed)
        {
            return ReplayTerminalEvidence(
                CommandExecutionStatus.Failed,
                failedAtUtc,
                resultPayload,
                canonicalReason,
                resultJudgement);
        }

        var result = TransitionTo(CommandExecutionStatus.Failed, failedAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        FailureReason = canonicalReason;
        ResultPayload = resultPayload;
        ResultJudgement = resultJudgement;
        return result;
    }

    internal RuntimeOperationResult TimeoutAt(
        DateTimeOffset timedOutAtUtc,
        string? resultPayload = null)
    {
        const string reason = "Command timed out.";
        if (Status == CommandExecutionStatus.TimedOut)
        {
            return ReplayTerminalEvidence(
                CommandExecutionStatus.TimedOut,
                timedOutAtUtc,
                resultPayload,
                reason,
                CommandResultJudgement.Unknown);
        }

        var result = TransitionTo(CommandExecutionStatus.TimedOut, timedOutAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        FailureReason = reason;
        ResultPayload = resultPayload;
        ResultJudgement = CommandResultJudgement.Unknown;
        return result;
    }

    internal RuntimeOperationResult Cancel(
        DateTimeOffset canceledAtUtc,
        string reason = "Command canceled.",
        string? resultPayload = null,
        CommandResultJudgement resultJudgement = CommandResultJudgement.Aborted)
    {
        ValidateResultJudgement(CommandExecutionStatus.Canceled, resultJudgement);
        var canonicalReason = Required(reason, nameof(reason));
        if (Status == CommandExecutionStatus.Canceled)
        {
            return ReplayTerminalEvidence(
                CommandExecutionStatus.Canceled,
                canceledAtUtc,
                resultPayload,
                canonicalReason,
                resultJudgement);
        }

        var result = TransitionTo(CommandExecutionStatus.Canceled, canceledAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        FailureReason = canonicalReason;
        ResultPayload = resultPayload;
        ResultJudgement = resultJudgement;
        return result;
    }

    internal RuntimeOperationResult Reject(string reason, DateTimeOffset rejectedAtUtc)
    {
        var canonicalReason = Required(reason, nameof(reason));
        if (Status == CommandExecutionStatus.Rejected)
        {
            return ReplayTerminalEvidence(
                CommandExecutionStatus.Rejected,
                rejectedAtUtc,
                null,
                canonicalReason,
                CommandResultJudgement.Unknown);
        }

        var result = TransitionTo(CommandExecutionStatus.Rejected, rejectedAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        FailureReason = canonicalReason;
        ResultJudgement = CommandResultJudgement.Unknown;
        return result;
    }

    private RuntimeOperationResult TransitionTo(CommandExecutionStatus target, DateTimeOffset utcNow)
    {
        if (Status == target)
        {
            var recordedAtUtc = target switch
            {
                CommandExecutionStatus.Running => StartedAtUtc,
                _ => CompletedAtUtc
            };
            return recordedAtUtc == utcNow
                ? RuntimeOperationResult.Accepted()
                : RuntimeOperationResult.Rejected(
                    "Runtime.CommandEvidenceConflict",
                    $"Command {Id} was replayed with a different transition timestamp.");
        }

        if (!CanTransition(Status, target))
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.CommandTransitionRejected",
                $"Command {Id} cannot transition from {Status} to {target}.");
        }

        Status = target;

        if (target == CommandExecutionStatus.Running)
        {
            StartedAtUtc = utcNow;
        }

        if (IsTerminal)
        {
            CompletedAtUtc = utcNow;
        }

        return RuntimeOperationResult.Accepted();
    }

    private RuntimeOperationResult ReplayTerminalEvidence(
        CommandExecutionStatus status,
        DateTimeOffset completedAtUtc,
        string? resultPayload,
        string? failureReason,
        CommandResultJudgement resultJudgement)
    {
        return Status == status
               && CompletedAtUtc == completedAtUtc
               && string.Equals(ResultPayload, resultPayload, StringComparison.Ordinal)
               && string.Equals(FailureReason, failureReason, StringComparison.Ordinal)
               && ResultJudgement == resultJudgement
            ? RuntimeOperationResult.Accepted()
            : RuntimeOperationResult.Rejected(
                "Runtime.CommandEvidenceConflict",
                $"Command {Id} was replayed with different terminal evidence.");
    }

    private static bool CanTransition(CommandExecutionStatus from, CommandExecutionStatus to)
    {
        if (from is CommandExecutionStatus.Completed
            or CommandExecutionStatus.Failed
            or CommandExecutionStatus.TimedOut
            or CommandExecutionStatus.Canceled
            or CommandExecutionStatus.Rejected)
        {
            return false;
        }

        return (from, to) switch
        {
            (CommandExecutionStatus.Pending, CommandExecutionStatus.Running) => true,
            (CommandExecutionStatus.Pending, CommandExecutionStatus.Rejected) => true,
            (CommandExecutionStatus.Pending, CommandExecutionStatus.Canceled) => true,
            (CommandExecutionStatus.Running, CommandExecutionStatus.Completed) => true,
            (CommandExecutionStatus.Running, CommandExecutionStatus.Failed) => true,
            (CommandExecutionStatus.Running, CommandExecutionStatus.TimedOut) => true,
            (CommandExecutionStatus.Running, CommandExecutionStatus.Canceled) => true,
            (CommandExecutionStatus.Running, CommandExecutionStatus.Rejected) => true,
            _ => false
        };
    }

    private static string? CanonicalOptional(string? value)
    {
        return value is null ? null : Required(value, nameof(value));
    }

    private static void ValidateResultJudgement(
        CommandExecutionStatus status,
        CommandResultJudgement? resultJudgement)
    {
        var isTerminal = status is CommandExecutionStatus.Completed
            or CommandExecutionStatus.Failed
            or CommandExecutionStatus.TimedOut
            or CommandExecutionStatus.Canceled
            or CommandExecutionStatus.Rejected;
        if (!isTerminal)
        {
            if (resultJudgement is not null)
            {
                throw new ArgumentException(
                    $"Non-terminal command status {status} cannot define a result judgement.",
                    nameof(resultJudgement));
            }

            return;
        }

        if (resultJudgement is null)
        {
            throw new ArgumentException(
                $"Terminal command status {status} requires a result judgement.",
                nameof(resultJudgement));
        }

        var valid = (status, resultJudgement.Value) switch
        {
            (CommandExecutionStatus.Completed, CommandResultJudgement.Passed) => true,
            (CommandExecutionStatus.Completed, CommandResultJudgement.Failed) => true,
            (CommandExecutionStatus.Completed, CommandResultJudgement.Aborted) => true,
            (CommandExecutionStatus.Completed, CommandResultJudgement.NotApplicable) => true,
            (CommandExecutionStatus.Failed, CommandResultJudgement.Unknown) => true,
            (CommandExecutionStatus.TimedOut, CommandResultJudgement.Unknown) => true,
            (CommandExecutionStatus.Canceled, CommandResultJudgement.Aborted) => true,
            (CommandExecutionStatus.Rejected, CommandResultJudgement.Unknown) => true,
            _ => false
        };

        if (!valid)
        {
            throw new ArgumentException(
                $"Result judgement {resultJudgement} is invalid for command status {status}.",
                nameof(resultJudgement));
        }
    }

    private static void ValidateRestoredLifecycle(RuntimeCommand command)
    {
        if (!Enum.IsDefined(command.Status))
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                command.Status,
                "Restored command execution status is not defined.");
        }

        ValidateResultJudgement(command.Status, command.ResultJudgement);

        if (command.AcceptedAtUtc is not null
                && command.AcceptedAtUtc < command.CreatedAtUtc
            || command.StartedAtUtc is not null
                && (command.AcceptedAtUtc is null || command.StartedAtUtc < command.AcceptedAtUtc)
            || command.CompletedAtUtc is not null
                && command.CompletedAtUtc < (command.StartedAtUtc ?? command.AcceptedAtUtc ?? command.CreatedAtUtc))
        {
            throw new ArgumentException("Restored command lifecycle timestamps are not ordered.");
        }

        var hasTerminalEvidence = command.CompletedAtUtc is not null
            || command.ResultPayload is not null
            || command.FailureReason is not null
            || command.ResultJudgement is not null;

        switch (command.Status)
        {
            case CommandExecutionStatus.Pending:
                if (command.StartedAtUtc is not null || hasTerminalEvidence)
                {
                    throw new ArgumentException(
                        "A pending restored command cannot contain start or terminal evidence.");
                }

                break;
            case CommandExecutionStatus.Running:
                if (command.AcceptedAtUtc is null
                    || command.StartedAtUtc is null
                    || hasTerminalEvidence)
                {
                    throw new ArgumentException(
                        "A running restored command requires acceptance and start evidence only.");
                }

                break;
            case CommandExecutionStatus.Completed:
                if (command.AcceptedAtUtc is null
                    || command.StartedAtUtc is null
                    || command.CompletedAtUtc is null
                    || command.FailureReason is not null)
                {
                    throw new ArgumentException(
                        "A completed restored command requires a complete successful lifecycle without failure evidence.");
                }

                break;
            case CommandExecutionStatus.Failed:
            case CommandExecutionStatus.TimedOut:
                if (command.AcceptedAtUtc is null
                    || command.StartedAtUtc is null
                    || command.CompletedAtUtc is null
                    || command.FailureReason is null)
                {
                    throw new ArgumentException(
                        "A failed or timed-out restored command requires a complete lifecycle and failure evidence.");
                }

                break;
            case CommandExecutionStatus.Canceled:
                if (command.CompletedAtUtc is null || command.FailureReason is null)
                {
                    throw new ArgumentException(
                        "A canceled restored command requires completion and cancellation evidence.");
                }

                break;
            case CommandExecutionStatus.Rejected:
                if (command.CompletedAtUtc is null
                    || command.FailureReason is null
                    || command.ResultPayload is not null)
                {
                    throw new ArgumentException(
                        "A rejected restored command requires rejection evidence and cannot contain a result payload.");
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(command),
                    command.Status,
                    "Unsupported restored command execution status.");
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
