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
        TraceCommandStatus status,
        TraceCommandSemanticOutcome? semanticOutcome,
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
        SemanticOutcome = semanticOutcome;
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

        ValidateSemanticOutcome(status, semanticOutcome);
    }

    public RuntimeCommandId RuntimeCommandId { get; }

    public Guid RuntimeStepId { get; }

    public string ActionId { get; }

    public TraceTargetKind TargetKind { get; }

    public string TargetId { get; }

    public string TargetCapabilityId { get; }

    public string CommandName { get; }

    public TraceCommandStatus Status { get; }

    public TraceCommandSemanticOutcome? SemanticOutcome { get; }

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

    private static void ValidateSemanticOutcome(
        TraceCommandStatus status,
        TraceCommandSemanticOutcome? semanticOutcome)
    {
        if (semanticOutcome is null)
        {
            return;
        }

        var isValid = (status, semanticOutcome) switch
        {
            (TraceCommandStatus.Completed, TraceCommandSemanticOutcome.Passed) => true,
            (TraceCommandStatus.Failed, TraceCommandSemanticOutcome.Failed) => true,
            (TraceCommandStatus.Canceled, TraceCommandSemanticOutcome.Aborted) => true,
            _ => false
        };
        if (!isValid)
        {
            throw new ArgumentException(
                $"Semantic outcome {semanticOutcome} is invalid for command status {status}.",
                nameof(semanticOutcome));
        }
    }
}
