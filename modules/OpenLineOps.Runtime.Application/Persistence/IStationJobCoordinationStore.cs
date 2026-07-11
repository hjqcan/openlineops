using OpenLineOps.Agent.Contracts;

namespace OpenLineOps.Runtime.Application.Persistence;

public interface IStationJobCoordinationStore
{
    ValueTask<bool> TryEnqueueAsync(
        StationJobRequested request,
        IReadOnlyCollection<ResourceLeaseChanged> resourceLeaseChanges,
        CancellationToken cancellationToken = default);

    ValueTask<StationJobCompleted?> GetCompletionAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    ValueTask<StationJobRecoveryRequired?> GetRecoveryRequiredAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);

    ValueTask RecordCompletionAsync(
        StationJobCompleted completion,
        CancellationToken cancellationToken = default);

    ValueTask RecordAcceptedAsync(
        StationJobAccepted accepted,
        CancellationToken cancellationToken = default);

    ValueTask RecordProgressAsync(
        StationJobProgressed progress,
        CancellationToken cancellationToken = default);

    ValueTask RecordRecoveryRequiredAsync(
        StationJobRecoveryRequired recoveryRequired,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<StationJobEventInboxItem>> ListEventsAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<StationJobOutboxItem>> ListPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);

    ValueTask<StationJobRequested?> GetDispatchRequestAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);

    ValueTask MarkPublishedAsync(
        Guid messageId,
        CancellationToken cancellationToken = default);

    ValueTask RecordPublishFailureAsync(
        Guid messageId,
        string failure,
        CancellationToken cancellationToken = default);

    ValueTask QuarantineJobAsync(
        Guid jobId,
        string reason,
        DateTimeOffset quarantinedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<StationJobQuarantineItem>> ListQuarantinedAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);
}

public sealed record StationJobOutboxItem(
    Guid MessageId,
    Guid JobId,
    string IdempotencyKey,
    string Kind,
    int Sequence,
    string PayloadJson,
    int AttemptCount,
    DateTimeOffset CreatedAtUtc);

public sealed record StationJobEventInboxItem(
    Guid MessageId,
    Guid JobId,
    string IdempotencyKey,
    string Kind,
    string PayloadJson,
    DateTimeOffset OccurredAtUtc);

public sealed record StationJobQuarantineItem(
    Guid MessageId,
    Guid JobId,
    string Kind,
    int Sequence,
    string Reason,
    DateTimeOffset QuarantinedAtUtc);
