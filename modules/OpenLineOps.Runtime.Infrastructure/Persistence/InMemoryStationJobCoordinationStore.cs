using System.Collections.Concurrent;
using System.Text.Json;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Persistence;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class InMemoryStationJobCoordinationStore : IStationJobCoordinationStore
{
    private static readonly JsonSerializerOptions JsonOptions = RuntimePersistenceJson.CreateOptions();
    private readonly ConcurrentDictionary<string, OutboxEntry> _outbox =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _completions =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, StationJobEventInboxItem> _events = [];

    public ValueTask<bool> TryEnqueueAsync(
        StationJobRequested request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        var payload = JsonSerializer.Serialize(request, JsonOptions);
        var entry = new OutboxEntry(request.MessageId, request.IdempotencyKey, payload, request.RequestedAtUtc);
        if (_outbox.TryAdd(request.IdempotencyKey, entry))
        {
            return ValueTask.FromResult(true);
        }

        EnsureSameJson(
            _outbox[request.IdempotencyKey].PayloadJson,
            payload,
            request.IdempotencyKey,
            "Station job");
        return ValueTask.FromResult(false);
    }

    public ValueTask<StationJobCompleted?> GetCompletionAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        return ValueTask.FromResult(
            _completions.TryGetValue(idempotencyKey, out var payload)
                ? JsonSerializer.Deserialize<StationJobCompleted>(payload, JsonOptions)
                  ?? throw new InvalidDataException("Station result Inbox payload is empty.")
                : null);
    }

    public ValueTask RecordCompletionAsync(
        StationJobCompleted completion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(completion);
        var payload = JsonSerializer.Serialize(completion, JsonOptions);
        if (!_completions.TryAdd(completion.IdempotencyKey, payload))
        {
            EnsureSameJson(
                _completions[completion.IdempotencyKey],
                payload,
                completion.IdempotencyKey,
                "Station completion");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RecordAcceptedAsync(
        StationJobAccepted accepted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        return RecordEventAsync(
            new StationJobEventInboxItem(
                accepted.MessageId,
                accepted.JobId,
                accepted.IdempotencyKey,
                nameof(StationJobAccepted),
                JsonSerializer.Serialize(accepted, JsonOptions),
                accepted.AcceptedAtUtc),
            cancellationToken);
    }

    public ValueTask RecordProgressAsync(
        StationJobProgressed progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return RecordEventAsync(
            new StationJobEventInboxItem(
                progress.MessageId,
                progress.JobId,
                progress.IdempotencyKey,
                nameof(StationJobProgressed),
                JsonSerializer.Serialize(progress, JsonOptions),
                progress.ProgressedAtUtc),
            cancellationToken);
    }

    public ValueTask<IReadOnlyCollection<StationJobEventInboxItem>> ListEventsAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyCollection<StationJobEventInboxItem> result = _events.Values
            .Where(item => item.JobId == jobId)
            .OrderBy(static item => item.OccurredAtUtc)
            .ThenBy(static item => item.MessageId)
            .ToArray();
        return ValueTask.FromResult(result);
    }

    public ValueTask<IReadOnlyCollection<StationJobOutboxItem>> ListPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        IReadOnlyCollection<StationJobOutboxItem> result = _outbox.Values
            .Where(static entry => !entry.Published)
            .OrderBy(static entry => entry.CreatedAtUtc)
            .ThenBy(static entry => entry.MessageId)
            .Take(maximumCount)
            .Select(static entry => new StationJobOutboxItem(
                entry.MessageId,
                entry.IdempotencyKey,
                entry.PayloadJson,
                entry.AttemptCount,
                entry.CreatedAtUtc))
            .ToArray();
        return ValueTask.FromResult(result);
    }

    public ValueTask MarkPublishedAsync(
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = Find(messageId);
        entry.Published = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask RecordPublishFailureAsync(
        Guid messageId,
        string failure,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);
        var entry = Find(messageId);
        entry.AttemptCount = checked(entry.AttemptCount + 1);
        entry.LastError = failure.Length <= 4096 ? failure : failure[..4096];
        return ValueTask.CompletedTask;
    }

    private OutboxEntry Find(Guid messageId) => _outbox.Values.SingleOrDefault(
        entry => entry.MessageId == messageId)
        ?? throw new InvalidOperationException(
            $"Station job outbox message {messageId:D} does not exist.");

    private ValueTask RecordEventAsync(
        StationJobEventInboxItem item,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_events.TryAdd(item.MessageId, item))
        {
            var existing = _events[item.MessageId];
            if (existing.JobId != item.JobId
                || !string.Equals(existing.IdempotencyKey, item.IdempotencyKey, StringComparison.Ordinal)
                || !string.Equals(existing.Kind, item.Kind, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Station event message {item.MessageId:D} was reused with different identity.");
            }

            EnsureSameJson(
                existing.PayloadJson,
                item.PayloadJson,
                item.MessageId.ToString("D"),
                "Station event message");
        }

        return ValueTask.CompletedTask;
    }

    private static void EnsureSameJson(
        string existing,
        string candidate,
        string idempotencyKey,
        string description)
    {
        using var existingJson = JsonDocument.Parse(existing);
        using var candidateJson = JsonDocument.Parse(candidate);
        if (!JsonElement.DeepEquals(existingJson.RootElement, candidateJson.RootElement))
        {
            throw new InvalidOperationException(
                $"{description} idempotency key '{idempotencyKey}' was reused with different evidence.");
        }
    }

    private sealed class OutboxEntry(
        Guid messageId,
        string idempotencyKey,
        string payloadJson,
        DateTimeOffset createdAtUtc)
    {
        public Guid MessageId { get; } = messageId;

        public string IdempotencyKey { get; } = idempotencyKey;

        public string PayloadJson { get; } = payloadJson;

        public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;

        public int AttemptCount { get; set; }

        public string? LastError { get; set; }

        public bool Published { get; set; }
    }
}
