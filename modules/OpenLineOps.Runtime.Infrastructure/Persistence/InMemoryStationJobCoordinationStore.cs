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
    private readonly object _dispatchGate = new();

    public ValueTask<bool> TryEnqueueAsync(
        StationJobRequested request,
        IReadOnlyCollection<ResourceLeaseChanged> resourceLeaseChanges,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(resourceLeaseChanges);
        var orderedChanges = ValidateDispatch(request, resourceLeaseChanges);
        lock (_dispatchGate)
        {
            var jobPayload = JsonSerializer.Serialize(request, JsonOptions);
            var jobAdded = !_outbox.ContainsKey(request.IdempotencyKey);
            for (var sequence = 0; sequence < orderedChanges.Length; sequence++)
            {
                AddOrValidate(new OutboxEntry(
                    orderedChanges[sequence].MessageId,
                    orderedChanges[sequence].IdempotencyKey,
                    nameof(ResourceLeaseChanged),
                    sequence,
                    JsonSerializer.Serialize(orderedChanges[sequence], JsonOptions),
                    request.RequestedAtUtc));
            }

            AddOrValidate(new OutboxEntry(
                request.MessageId,
                request.IdempotencyKey,
                nameof(StationJobRequested),
                orderedChanges.Length,
                jobPayload,
                request.RequestedAtUtc));
            return ValueTask.FromResult(jobAdded);
        }
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
            .ThenBy(static entry => entry.Sequence)
            .ThenBy(static entry => entry.MessageId)
            .Take(maximumCount)
            .Select(static entry => new StationJobOutboxItem(
                entry.MessageId,
                entry.IdempotencyKey,
                entry.Kind,
                entry.Sequence,
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

    private void AddOrValidate(OutboxEntry candidate)
    {
        if (_outbox.TryAdd(candidate.IdempotencyKey, candidate))
        {
            return;
        }

        var existing = _outbox[candidate.IdempotencyKey];
        if (existing.MessageId != candidate.MessageId
            || !string.Equals(existing.Kind, candidate.Kind, StringComparison.Ordinal)
            || existing.Sequence != candidate.Sequence)
        {
            throw new InvalidOperationException(
                $"Station dispatch idempotency key '{candidate.IdempotencyKey}' was reused with different identity.");
        }

        EnsureSameJson(
            existing.PayloadJson,
            candidate.PayloadJson,
            candidate.IdempotencyKey,
            "Station dispatch message");
    }

    private static ResourceLeaseChanged[] ValidateDispatch(
        StationJobRequested request,
        IReadOnlyCollection<ResourceLeaseChanged> changes)
    {
        var expected = request.ResourceFences
            .OrderBy(static fence => fence.ResourceKind, StringComparer.Ordinal)
            .ThenBy(static fence => fence.ResourceId, StringComparer.Ordinal)
            .Select(fence => OpenLineOps.Runtime.Application.Runs.StationDispatchMessageIdentity
                .CreateLeaseGranted(request, fence))
            .ToArray();
        var supplied = changes
            .OrderBy(static change => change.ResourceKind, StringComparer.Ordinal)
            .ThenBy(static change => change.ResourceId, StringComparer.Ordinal)
            .ToArray();
        if (expected.Length != supplied.Length)
        {
            throw new InvalidDataException(
                "Station dispatch resource lease changes do not match its fences.");
        }

        for (var index = 0; index < expected.Length; index++)
        {
            if (expected[index] != supplied[index])
            {
                throw new InvalidDataException(
                    "Station dispatch resource lease change evidence is not canonical.");
            }
        }

        return supplied;
    }

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
        string kind,
        int sequence,
        string payloadJson,
        DateTimeOffset createdAtUtc)
    {
        public Guid MessageId { get; } = messageId;

        public string IdempotencyKey { get; } = idempotencyKey;

        public string Kind { get; } = kind;

        public int Sequence { get; } = sequence;

        public string PayloadJson { get; } = payloadJson;

        public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;

        public int AttemptCount { get; set; }

        public string? LastError { get; set; }

        public bool Published { get; set; }
    }
}
