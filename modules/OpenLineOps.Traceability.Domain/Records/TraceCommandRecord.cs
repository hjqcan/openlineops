using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Traceability.Domain.Identifiers;

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
        ExecutionStatus executionStatus,
        ResultJudgement resultJudgement,
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
        TraceCommandExecutionAxes.Validate(executionStatus, resultJudgement);
        ExecutionStatus = executionStatus;
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

        if ((ExecutionStatus == ExecutionStatus.Completed) == (FailureReason is not null))
        {
            throw new ArgumentException(
                "Completed command execution cannot contain failure evidence; every unsuccessful execution requires it.",
                nameof(failureReason));
        }
    }

    public RuntimeCommandId RuntimeCommandId { get; }

    public Guid RuntimeStepId { get; }

    public string ActionId { get; }

    public TraceTargetKind TargetKind { get; }

    public string TargetId { get; }

    public string TargetCapabilityId { get; }

    public string CommandName { get; }

    public ExecutionStatus ExecutionStatus { get; }

    public ResultJudgement ResultJudgement { get; }

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

}

internal static class TraceCommandExecutionAxes
{
    public static void Validate(
        ExecutionStatus executionStatus,
        ResultJudgement resultJudgement)
    {
        if (!Enum.IsDefined(executionStatus)
            || executionStatus is ExecutionStatus.Pending or ExecutionStatus.Running)
        {
            throw new ArgumentException(
                $"Trace command execution status {executionStatus} must be terminal.",
                nameof(executionStatus));
        }

        if (!Enum.IsDefined(resultJudgement))
        {
            throw new ArgumentException(
                $"Trace command result judgement {resultJudgement} is unsupported.",
                nameof(resultJudgement));
        }

        var isValid = (executionStatus, resultJudgement) switch
        {
            (ExecutionStatus.Completed, ResultJudgement.Passed) => true,
            (ExecutionStatus.Completed, ResultJudgement.Failed) => true,
            (ExecutionStatus.Completed, ResultJudgement.Aborted) => true,
            (ExecutionStatus.Completed, ResultJudgement.NotApplicable) => true,
            (ExecutionStatus.Canceled, ResultJudgement.Aborted) => true,
            (ExecutionStatus.Failed, ResultJudgement.Unknown) => true,
            (ExecutionStatus.TimedOut, ResultJudgement.Unknown) => true,
            (ExecutionStatus.Rejected, ResultJudgement.Unknown) => true,
            _ => false
        };
        if (!isValid)
        {
            throw new ArgumentException(
                $"Result judgement {resultJudgement} is invalid for command execution status {executionStatus}.",
                nameof(resultJudgement));
        }
    }
}
