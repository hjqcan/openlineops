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

    public ValueTask<bool> TryEnqueueAsync(
        MaterialArrived message,
        CancellationToken cancellationToken = default)
    {
        StationMessageContract.Validate(message);
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
                    message.MessageId,
                    message.IdempotencyKey,
                    payload,
                    message.ArrivedAtUtc));
            _byIdempotencyKey.Add(message.IdempotencyKey, message.MessageId);
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<IReadOnlyCollection<StationMaterialArrivalOutboxItem>> ListPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyCollection<StationMaterialArrivalOutboxItem> result = _byMessageId.Values
                .Where(static entry => entry.PublishedAtUtc is null)
                .OrderBy(static entry => entry.CreatedAtUtc)
                .ThenBy(static entry => entry.MessageId)
                .Take(maximumCount)
                .Select(static entry => new StationMaterialArrivalOutboxItem(
                    entry.MessageId,
                    entry.IdempotencyKey,
                    entry.PayloadJson,
                    entry.CreatedAtUtc,
                    entry.AttemptCount))
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
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var entry = Find(messageId);
            entry.AttemptCount = checked(entry.AttemptCount + 1);
            entry.LastError = failure.Length <= 4096 ? failure : failure[..4096];
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

    private sealed class Entry(
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
        public DateTimeOffset? PublishedAtUtc { get; set; }
    }
}
