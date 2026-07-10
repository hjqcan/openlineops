namespace OpenLineOps.Runtime.Api.Models;

public sealed record RuntimeTargetStatusResponse(
    string StationSystemId,
    Guid SessionId,
    string ActionId,
    string TargetKind,
    string TargetId,
    string CommandStatus,
    DateTimeOffset LastTransitionAtUtc,
    bool IsTerminal,
    string? FailureReason);
