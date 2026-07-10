namespace OpenLineOps.Runtime.Api.Models;

public sealed record RuntimeStepResponse(
    Guid StepId,
    string NodeId,
    string DisplayName,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureReason,
    string ActionId,
    string TargetKind,
    string TargetId);
