using System.Text.Json;

namespace OpenLineOps.Integration.Api.Models;

public sealed record CreateWorkOrderRequest(
    string WorkOrderId,
    string ProductModelId,
    int TargetQuantity,
    string FactId,
    DateTimeOffset OccurredAtUtc);

public sealed record TransitionWorkOrderRequest(
    string FactId,
    string Kind,
    DateTimeOffset OccurredAtUtc,
    string? Reason);

public sealed record WorkOrderFactResponse(
    string FactId,
    long Sequence,
    string Kind,
    string ResultingStatus,
    DateTimeOffset OccurredAtUtc,
    string ActorId,
    string? ProductModelId,
    int? TargetQuantity,
    string? Reason);

public sealed record WorkOrderResponse(
    string WorkOrderId,
    string ProductModelId,
    int TargetQuantity,
    string Status,
    IReadOnlyList<WorkOrderFactResponse> Facts);

public sealed record SubmitWorkRequestRequest(
    string WorkRequestId,
    string WorkOrderId,
    string Kind,
    string StationId,
    DateTimeOffset OccurredAtUtc,
    JsonElement Payload);

public sealed record WorkRequestResponse(
    string WorkRequestId,
    string WorkOrderId,
    string Kind,
    string Status,
    string SourceSystem,
    DateTimeOffset OccurredAtUtc,
    JsonElement Payload,
    DateTimeOffset ReceivedAtUtc,
    string InboxState,
    string? ResponseMessageId,
    DateTimeOffset? CompletedAtUtc);

public sealed record WorkResponseResponse(
    string WorkResponseId,
    string WorkRequestId,
    string WorkOrderId,
    string Status,
    string TargetSystem,
    DateTimeOffset OccurredAtUtc,
    JsonElement Payload,
    long Sequence,
    int AttemptCount,
    DateTimeOffset NextAttemptAtUtc,
    string? LastError,
    DateTimeOffset? DeliveredAtUtc,
    DateTimeOffset? DeadLetteredAtUtc);

public sealed record WorkRequestProcessingResponse(
    string Outcome,
    WorkResponseResponse? Response);

public sealed record DeadLetterResponse(
    long Sequence,
    string MessageId,
    string CorrelationId,
    int AttemptCount,
    string LastError,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset DeadLetteredAtUtc);

public sealed record ReplayOutboxRequest(string Reason);

public sealed record OutboxReplayAuditResponse(
    long Sequence,
    string MessageId,
    string ActorId,
    string Reason,
    DateTimeOffset ReplayedAtUtc,
    int PreviousAttemptCount);
