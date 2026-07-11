using OpenLineOps.Traceability.Domain.Identifiers;
using CommandResultJudgement = OpenLineOps.Runtime.Contracts.ResultJudgement;

namespace OpenLineOps.Traceability.Domain.Records;

public sealed record TraceCommandRecord
{
    public TraceCommandRecord(
        RuntimeCommandId runtimeCommandId,
        Guid runtimeStepId,
        string actionId,
        TraceTargetKind targetKind,
        string targetId,
        string targetCapabilityId,
        string commandName,
        TraceCommandStatus status,
        CommandResultJudgement? resultJudgement,
        DateTimeOffset createdAtUtc,
        DateTimeOffset deadlineAtUtc,
        DateTimeOffset? acceptedAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? completedAtUtc,
        string? resultPayload,
        string? failureReason)
    {
        RuntimeCommandId = runtimeCommandId;
        RuntimeStepId = TraceabilityIdGuard.NotEmpty(runtimeStepId, nameof(runtimeStepId));
        ActionId = TraceabilityIdGuard.NotBlank(actionId, nameof(actionId));
        TargetKind = targetKind;
        TargetId = TraceabilityIdGuard.NotBlank(targetId, nameof(targetId));
        TargetCapabilityId = TraceabilityIdGuard.NotBlank(targetCapabilityId, nameof(targetCapabilityId));
        CommandName = TraceabilityIdGuard.NotBlank(commandName, nameof(commandName));
        Status = status;
        ResultJudgement = resultJudgement;
        CreatedAtUtc = RequiredTimestamp(createdAtUtc, nameof(createdAtUtc));
        DeadlineAtUtc = RequiredTimestamp(deadlineAtUtc, nameof(deadlineAtUtc));
        AcceptedAtUtc = acceptedAtUtc;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        ResultPayload = TraceabilityIdGuard.OptionalText(resultPayload);
        FailureReason = TraceabilityIdGuard.OptionalText(failureReason);

        if (DeadlineAtUtc <= CreatedAtUtc)
        {
            throw new ArgumentException("Command deadline must be later than creation time.", nameof(deadlineAtUtc));
        }

        if (acceptedAtUtc < createdAtUtc
            || startedAtUtc < (acceptedAtUtc ?? createdAtUtc)
            || completedAtUtc < (startedAtUtc ?? acceptedAtUtc ?? createdAtUtc))
        {
            throw new ArgumentException("Command timestamps must be chronological.", nameof(completedAtUtc));
        }

        ValidateResultJudgement(status, resultJudgement);
    }

    public RuntimeCommandId RuntimeCommandId { get; }

    public Guid RuntimeStepId { get; }

    public string ActionId { get; }

    public TraceTargetKind TargetKind { get; }

    public string TargetId { get; }

    public string TargetCapabilityId { get; }

    public string CommandName { get; }

    public TraceCommandStatus Status { get; }

    public CommandResultJudgement? ResultJudgement { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset DeadlineAtUtc { get; }

    public DateTimeOffset? AcceptedAtUtc { get; }

    public DateTimeOffset? StartedAtUtc { get; }

    public DateTimeOffset? CompletedAtUtc { get; }

    public string? ResultPayload { get; }

    public string? FailureReason { get; }

    private static DateTimeOffset RequiredTimestamp(DateTimeOffset value, string parameterName)
    {
        return value == default
            ? throw new ArgumentException("Timestamp is required.", parameterName)
            : value;
    }

    private static void ValidateResultJudgement(
        TraceCommandStatus status,
        CommandResultJudgement? resultJudgement)
    {
        var isTerminal = status is TraceCommandStatus.Completed
            or TraceCommandStatus.Failed
            or TraceCommandStatus.TimedOut
            or TraceCommandStatus.Canceled
            or TraceCommandStatus.Rejected;
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

        var isValid = (status, resultJudgement.Value) switch
        {
            (TraceCommandStatus.Completed, CommandResultJudgement.Passed) => true,
            (TraceCommandStatus.Completed, CommandResultJudgement.Failed) => true,
            (TraceCommandStatus.Completed, CommandResultJudgement.Aborted) => true,
            (TraceCommandStatus.Completed, CommandResultJudgement.NotApplicable) => true,
            (TraceCommandStatus.Failed, CommandResultJudgement.Unknown) => true,
            (TraceCommandStatus.TimedOut, CommandResultJudgement.Unknown) => true,
            (TraceCommandStatus.Canceled, CommandResultJudgement.Unknown) => true,
            (TraceCommandStatus.Rejected, CommandResultJudgement.Unknown) => true,
            _ => false
        };
        if (!isValid)
        {
            throw new ArgumentException(
                $"Result judgement {resultJudgement} is invalid for command status {status}.",
                nameof(resultJudgement));
        }
    }
}
