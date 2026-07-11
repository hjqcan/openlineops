using OpenLineOps.Agent.Contracts;

namespace OpenLineOps.Runtime.Application.Persistence;

public interface IStationJobCoordinationStore
{
    ValueTask<bool> TryEnqueueAsync(
        StationJobRequested request,
        CancellationToken cancellationToken = default);

    ValueTask<StationJobCompleted?> GetCompletionAsync(
        string idempotencyKey,
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

    ValueTask<IReadOnlyCollection<StationJobEventInboxItem>> ListEventsAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<StationJobOutboxItem>> ListPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);

    ValueTask MarkPublishedAsync(
        Guid messageId,
        CancellationToken cancellationToken = default);

    ValueTask RecordPublishFailureAsync(
        Guid messageId,
        string failure,
        CancellationToken cancellationToken = default);
}

public sealed record StationJobOutboxItem(
    Guid MessageId,
    string IdempotencyKey,
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
