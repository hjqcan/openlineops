using OpenLineOps.Operations.Domain.Shared.Enums;

namespace OpenLineOps.Operations.Application.Contract.Alarms;

public sealed record AlarmDetails(
    string Id,
    string StationId,
    string Source,
    string? SourceId,
    AlarmSeverity Severity,
    AlarmStatus Status,
    string Title,
    string Description,
    DateTimeOffset RaisedAtUtc,
    string? AcknowledgedBy,
    DateTimeOffset? AcknowledgedAtUtc,
    string? ResolvedBy,
    DateTimeOffset? ResolvedAtUtc,
    string? ResolutionNote,
    string? DefinitionId = null,
    long Version = 1,
    bool SourceActive = true,
    string? SourceClearedBy = null,
    DateTimeOffset? SourceClearedAtUtc = null,
    string? SourceClearanceNote = null,
    bool IsLatching = false,
    string? AcknowledgementComment = null,
    string? ShelvedBy = null,
    string? ShelfComment = null,
    DateTimeOffset? ShelvedAtUtc = null,
    DateTimeOffset? ShelvedUntilUtc = null,
    bool IsShelved = false,
    string? SuppressionSource = null,
    string? SuppressionReason = null,
    string? SuppressedBy = null,
    DateTimeOffset? SuppressedAtUtc = null,
    DateTimeOffset? SuppressedUntilUtc = null,
    bool IsSuppressed = false,
    bool BuzzerActive = false,
    bool EscalationDue = false,
    AlarmPolicyAction EscalationAction = AlarmPolicyAction.None);

public sealed record RaiseAlarmRequest(
    string Id,
    string StationId,
    string Source,
    string? SourceId,
    AlarmSeverity Severity,
    string Title,
    string Description,
    DateTimeOffset? RaisedAtUtc = null,
    string? DefinitionId = null,
    string? CommandId = null,
    string? ActorId = null);

public sealed record AcknowledgeAlarmRequest(
    string AcknowledgedBy,
    string AcknowledgementComment = "Acknowledged.",
    string? CommandId = null,
    long? ExpectedVersion = null);

public sealed record ClearAlarmSourceRequest(
    string SourceActor,
    string StationId,
    string ClearanceNote,
    string? CommandId = null,
    long? ExpectedVersion = null);

public sealed record RegisterAlarmDefinitionRequest(
    string Id,
    string StationId,
    string Source,
    AlarmSeverity Severity,
    string Title,
    string Description,
    bool IsLatching,
    bool RequiresBuzzer,
    int MaximumShelfSeconds,
    int? EscalationDelaySeconds,
    AlarmPolicyAction EscalationAction,
    string CreatedBy,
    string CommandId);

public sealed record AlarmDefinitionDetails(
    string Id,
    string StationId,
    string Source,
    AlarmSeverity Severity,
    string Title,
    string Description,
    bool IsLatching,
    bool RequiresBuzzer,
    int MaximumShelfSeconds,
    int? EscalationDelaySeconds,
    AlarmPolicyAction EscalationAction,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc);

public sealed record AlarmDefinitionCommandResult(
    bool Succeeded,
    string Code,
    string Message,
    AlarmDefinitionDetails? Definition,
    bool Replayed = false);

public sealed record RaiseAlarmCommandResult(
    bool Succeeded,
    string Code,
    string Message,
    AlarmDetails? Alarm,
    bool Replayed = false);

public sealed record ShelfAlarmRequest(
    string ActorId,
    string Comment,
    DateTimeOffset ShelvedUntilUtc,
    string CommandId,
    long ExpectedVersion);

public sealed record SuppressAlarmRequest(
    string ActorId,
    string SuppressionSource,
    string Reason,
    DateTimeOffset SuppressedUntilUtc,
    string CommandId,
    long ExpectedVersion);

public sealed record AlarmLifecycleFactDetails(
    long Sequence,
    string FactId,
    string AlarmId,
    long AlarmVersion,
    string CommandId,
    AlarmLifecycleAction Action,
    string ActorId,
    DateTimeOffset OccurredAtUtc,
    string PayloadJson,
    string ContentSha256);
