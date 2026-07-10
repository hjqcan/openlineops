namespace OpenLineOps.Runtime.Api.Models;

public sealed record RuntimeCommandResponse(
    Guid CommandId,
    Guid StepId,
    string TargetCapability,
    string CommandName,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset DeadlineAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ResultPayload,
    string? FailureReason,
    string ActionId);
