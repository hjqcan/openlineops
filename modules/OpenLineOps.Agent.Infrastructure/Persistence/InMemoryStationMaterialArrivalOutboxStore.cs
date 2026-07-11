using System.Text.Json;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;

namespace OpenLineOps.Agent.Infrastructure.Persistence;

public sealed class InMemoryStationMaterialArrivalOutboxStore :
    IStationMaterialArrivalOutboxStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        StationJobPersistenceJson.CreateOptions();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _byMessageId = [];
    private readonly Dictionary<string, Guid> _byIdempotencyKey = new(StringComparer.Ordinal);
    private long _nextSequence;

    public ValueTask<bool> TryEnqueueAsync(
        MaterialArrived message,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken = default)
    {
        StationMessageContract.Validate(message);
        ValidateUtc(receivedAtUtc, nameof(receivedAtUtc));
        cancellationToken.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(message, JsonOptions);
        lock (_gate)
        {
            if (_byMessageId.TryGetValue(message.MessageId, out var existing)
                || (_byIdempotencyKey.TryGetValue(message.IdempotencyKey, out var existingId)
                    && _byMessageId.TryGetValue(existingId, out existing)))
            {
                if (existing.MessageId != message.MessageId
                    || !string.Equals(
                        existing.IdempotencyKey,
                        message.IdempotencyKey,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Material arrival outbox identity was reused with different evidence.");
                }

                EnsureSameJson(existing.PayloadJson, payload);
                return ValueTask.FromResult(false);
            }

            _byMessageId.Add(
                message.MessageId,
                new Entry(
                    checked(++_nextSequence),
                    message.MessageId,
                    message.IdempotencyKey,
                    payload,
                    receivedAtUtc));
            _byIdempotencyKey.Add(message.IdempotencyKey, message.MessageId);
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<IReadOnlyCollection<StationMaterialArrivalOutboxItem>> ListPendingAsync(
        int maximumCount,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        ValidateUtc(nowUtc, nameof(nowUtc));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var ordered = _byMessageId.Values
                .Where(static entry => entry.PublishedAtUtc is null)
                .Where(static entry => entry.QuarantinedAtUtc is null)
                .OrderBy(static entry => entry.Sequence)
                .Take(maximumCount)
                .ToArray();
            IReadOnlyCollection<StationMaterialArrivalOutboxItem> result = ordered
                .TakeWhile(entry => entry.NextAttemptAtUtc <= nowUtc)
                .Select(static entry => new StationMaterialArrivalOutboxItem(
                    entry.Sequence,
                    entry.MessageId,
                    entry.IdempotencyKey,
                    entry.PayloadJson,
                    entry.CreatedAtUtc,
                    entry.AttemptCount,
                    entry.NextAttemptAtUtc))
                .ToArray();
            return ValueTask.FromResult(result);
        }
    }

    public ValueTask MarkPublishedAsync(
        Guid messageId,
        DateTimeOffset publishedAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (publishedAtUtc == default || publishedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Material publication time must be a non-default UTC value.",
                nameof(publishedAtUtc));
        }

        lock (_gate)
        {
            Find(messageId).PublishedAtUtc = publishedAtUtc;
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask RecordPublishFailureAsync(
        Guid messageId,
        string failure,
        DateTimeOffset nextAttemptAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);
        ValidateUtc(nextAttemptAtUtc, nameof(nextAttemptAtUtc));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var entry = Find(messageId);
            entry.AttemptCount = checked(entry.AttemptCount + 1);
            entry.LastError = failure.Length <= 4096 ? failure : failure[..4096];
            entry.NextAttemptAtUtc = nextAttemptAtUtc;
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask QuarantineAsync(
        Guid messageId,
        string failure,
        DateTimeOffset quarantinedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);
        ValidateUtc(quarantinedAtUtc, nameof(quarantinedAtUtc));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var entry = Find(messageId);
            entry.AttemptCount = checked(entry.AttemptCount + 1);
            entry.LastError = failure.Length <= 4096 ? failure : failure[..4096];
            entry.QuarantinedAtUtc = quarantinedAtUtc;
            return ValueTask.CompletedTask;
        }
    }

    private Entry Find(Guid messageId) => _byMessageId.GetValueOrDefault(messageId)
        ?? throw new InvalidOperationException(
            $"Material arrival outbox message {messageId:D} does not exist.");

    private static void EnsureSameJson(string existing, string candidate)
    {
        using var existingJson = JsonDocument.Parse(existing);
        using var candidateJson = JsonDocument.Parse(candidate);
        if (!JsonElement.DeepEquals(existingJson.RootElement, candidateJson.RootElement))
        {
            throw new InvalidOperationException(
                "Material arrival outbox identity was reused with different evidence.");
        }
    }

    private static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Station material arrival outbox timestamp must be non-default UTC.",
                parameterName);
        }
    }

    private sealed class Entry(
        long sequence,
        Guid messageId,
        string idempotencyKey,
        string payloadJson,
        DateTimeOffset createdAtUtc)
    {
        public long Sequence { get; } = sequence;
        public Guid MessageId { get; } = messageId;
        public string IdempotencyKey { get; } = idempotencyKey;
        public string PayloadJson { get; } = payloadJson;
        public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;
        public DateTimeOffset NextAttemptAtUtc { get; set; } = createdAtUtc;
        public int AttemptCount { get; set; }
        public string? LastError { get; set; }
        public DateTimeOffset? PublishedAtUtc { get; set; }
        public DateTimeOffset? QuarantinedAtUtc { get; set; }
    }
}
