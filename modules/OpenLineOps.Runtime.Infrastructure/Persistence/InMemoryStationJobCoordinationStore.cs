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
                    request.JobId,
                    orderedChanges[sequence].IdempotencyKey,
                    nameof(ResourceLeaseChanged),
                    sequence,
                    JsonSerializer.Serialize(orderedChanges[sequence], JsonOptions),
                    request.RequestedAtUtc));
            }

            AddOrValidate(new OutboxEntry(
                request.MessageId,
                request.JobId,
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

    public ValueTask<StationJobRecoveryRequired?> GetRecoveryRequiredAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_dispatchGate)
        {
            var item = _events.Values.SingleOrDefault(entry =>
                entry.JobId == jobId
                && string.Equals(
                    entry.Kind,
                    nameof(StationJobRecoveryRequired),
                    StringComparison.Ordinal));
            return ValueTask.FromResult(item is null
                ? null
                : JsonSerializer.Deserialize<StationJobRecoveryRequired>(item.PayloadJson, JsonOptions)
                  ?? throw new InvalidDataException(
                      "Station recovery-required Inbox payload is empty."));
        }
    }

    public ValueTask RecordCompletionAsync(
        StationJobCompleted completion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(completion);
        StationMessageContract.Validate(completion);
        lock (_dispatchGate)
        {
            var request = RequireRequest(completion.JobId);
            ValidateResultIdentity(
                request,
                completion.JobId,
                completion.IdempotencyKey,
                completion.AgentId,
                completion.StationId);
            if (completion.RuntimeSessionId != request.RuntimeSessionId)
            {
                throw new InvalidDataException(
                    "Station completion Runtime Session does not match its dispatch request.");
            }

            var latest = RequireAcceptedAndLatestEvent(request);
            if (completion.CompletedAtUtc < latest
                || _events.Values.Any(item => item.JobId == completion.JobId
                    && string.Equals(
                        item.Kind,
                        nameof(StationJobRecoveryRequired),
                        StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    "Station completion timestamp precedes its persisted event timeline.");
            }

            var payload = JsonSerializer.Serialize(completion, JsonOptions);
            if (!_completions.TryAdd(completion.IdempotencyKey, payload))
            {
                EnsureSameJson(
                    _completions[completion.IdempotencyKey],
                    payload,
                    completion.IdempotencyKey,
                    "Station completion");
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RecordAcceptedAsync(
        StationJobAccepted accepted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        StationMessageContract.Validate(accepted);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_dispatchGate)
        {
            var item = new StationJobEventInboxItem(
                accepted.MessageId,
                accepted.JobId,
                accepted.IdempotencyKey,
                nameof(StationJobAccepted),
                JsonSerializer.Serialize(accepted, JsonOptions),
                accepted.AcceptedAtUtc);
            if (_events.ContainsKey(item.MessageId))
            {
                RecordEventLocked(item);
                return ValueTask.CompletedTask;
            }

            var request = RequireRequest(accepted.JobId);
            ValidateResultIdentity(
                request,
                accepted.JobId,
                accepted.IdempotencyKey,
                accepted.AgentId,
                accepted.StationId);
            if (accepted.AcceptedAtUtc < request.RequestedAtUtc
                || _events.Values.Any(item => item.JobId == accepted.JobId))
            {
                throw new InvalidDataException(
                    "Station acceptance is out of order for its dispatch request.");
            }

            RecordEventLocked(item);
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask RecordProgressAsync(
        StationJobProgressed progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        StationMessageContract.Validate(progress);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_dispatchGate)
        {
            var item = new StationJobEventInboxItem(
                progress.MessageId,
                progress.JobId,
                progress.IdempotencyKey,
                nameof(StationJobProgressed),
                JsonSerializer.Serialize(progress, JsonOptions),
                progress.ProgressedAtUtc);
            if (_events.ContainsKey(item.MessageId))
            {
                RecordEventLocked(item);
                return ValueTask.CompletedTask;
            }

            var request = RequireRequest(progress.JobId);
            ValidateResultIdentity(
                request,
                progress.JobId,
                progress.IdempotencyKey,
                progress.AgentId,
                progress.StationId);
            if (_completions.ContainsKey(progress.IdempotencyKey)
                || _events.Values.Any(existing => existing.JobId == progress.JobId
                    && string.Equals(
                        existing.Kind,
                        nameof(StationJobRecoveryRequired),
                        StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    "Station progress cannot follow terminal completion or recovery evidence.");
            }

            var latest = RequireAcceptedAndLatestEvent(request);
            var previousPercent = _events.Values
                .Where(item => item.JobId == progress.JobId
                    && string.Equals(item.Kind, nameof(StationJobProgressed), StringComparison.Ordinal))
                .Select(item => JsonSerializer.Deserialize<StationJobProgressed>(item.PayloadJson, JsonOptions)
                    ?? throw new InvalidDataException("Station progress Inbox payload is empty."))
                .Select(static item => item.Percent)
                .DefaultIfEmpty(0)
                .Max();
            if (progress.ProgressedAtUtc < latest || progress.Percent < previousPercent)
            {
                throw new InvalidDataException("Station progress is not monotonic.");
            }

            RecordEventLocked(item);
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask RecordRecoveryRequiredAsync(
        StationJobRecoveryRequired recoveryRequired,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recoveryRequired);
        StationMessageContract.Validate(recoveryRequired);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_dispatchGate)
        {
            var item = new StationJobEventInboxItem(
                recoveryRequired.MessageId,
                recoveryRequired.JobId,
                recoveryRequired.IdempotencyKey,
                nameof(StationJobRecoveryRequired),
                JsonSerializer.Serialize(recoveryRequired, JsonOptions),
                recoveryRequired.DetectedAtUtc);
            if (_events.ContainsKey(item.MessageId))
            {
                RecordEventLocked(item);
                return ValueTask.CompletedTask;
            }

            var request = RequireRequest(recoveryRequired.JobId);
            ValidateResultIdentity(
                request,
                recoveryRequired.JobId,
                recoveryRequired.JobIdempotencyKey,
                recoveryRequired.AgentId,
                recoveryRequired.StationId);
            if (recoveryRequired.ProductionRunId != request.ProductionRunId
                || recoveryRequired.RuntimeSessionId != request.RuntimeSessionId
                || !string.Equals(
                    recoveryRequired.OperationRunId,
                    request.OperationRunId,
                    StringComparison.Ordinal)
                || _completions.ContainsKey(request.IdempotencyKey)
                || _events.Values.Any(existing => existing.JobId == recoveryRequired.JobId
                    && string.Equals(
                        existing.Kind,
                        nameof(StationJobRecoveryRequired),
                        StringComparison.Ordinal))
                || recoveryRequired.DetectedAtUtc < RequireAcceptedAndLatestEvent(request))
            {
                throw new InvalidDataException(
                    "Station recovery-required evidence does not exactly follow its dispatch timeline.");
            }

            RecordEventLocked(item);
            return ValueTask.CompletedTask;
        }
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
            .Where(static entry => !entry.Published && entry.QuarantinedAtUtc is null)
            .GroupBy(static entry => entry.JobId)
            .Select(static group => group
                .OrderBy(static entry => entry.Sequence)
                .ThenBy(static entry => entry.MessageId)
                .First())
            .OrderBy(static entry => entry.CreatedAtUtc)
            .ThenBy(static entry => entry.Sequence)
            .ThenBy(static entry => entry.MessageId)
            .Take(maximumCount)
            .Select(static entry => new StationJobOutboxItem(
                entry.MessageId,
                entry.JobId,
                entry.IdempotencyKey,
                entry.Kind,
                entry.Sequence,
                entry.PayloadJson,
                entry.AttemptCount,
                entry.CreatedAtUtc))
            .ToArray();
        return ValueTask.FromResult(result);
    }

    public ValueTask<StationJobRequested?> GetDispatchRequestAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("Station Job id cannot be empty.", nameof(jobId));
        }

        lock (_dispatchGate)
        {
            var entry = _outbox.Values.SingleOrDefault(item =>
                item.JobId == jobId
                && string.Equals(item.Kind, nameof(StationJobRequested), StringComparison.Ordinal));
            return ValueTask.FromResult(entry is null
                ? null
                : JsonSerializer.Deserialize<StationJobRequested>(entry.PayloadJson, JsonOptions)
                  ?? throw new InvalidDataException("Station dispatch request payload is empty."));
        }
    }

    public ValueTask MarkPublishedAsync(
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = Find(messageId);
        if (entry.QuarantinedAtUtc is not null)
        {
            throw new InvalidOperationException(
                $"Station job outbox message {messageId:D} is quarantined and cannot be published.");
        }

        entry.Published = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask QuarantineJobAsync(
        Guid jobId,
        string reason,
        DateTimeOffset quarantinedAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (quarantinedAtUtc == default || quarantinedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Station dispatch quarantine timestamp must be non-default UTC.",
                nameof(quarantinedAtUtc));
        }

        lock (_dispatchGate)
        {
            var entries = _outbox.Values.Where(entry => entry.JobId == jobId).ToArray();
            if (entries.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Station dispatch Job {jobId:D} does not exist.");
            }

            var unpublished = entries.Where(static entry => !entry.Published).ToArray();
            if (unpublished.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Station dispatch Job {jobId:D} has no unpublished messages to quarantine.");
            }

            var existingEvidence = unpublished
                .Where(static entry => entry.QuarantinedAtUtc is not null)
                .ToArray();
            if (existingEvidence.Any(entry => !string.Equals(
                    entry.QuarantineReason,
                    reason,
                    StringComparison.Ordinal))
                || existingEvidence.Select(static entry => entry.QuarantinedAtUtc)
                    .Distinct()
                    .Count() > 1)
            {
                throw new InvalidOperationException(
                    $"Station dispatch Job {jobId:D} already has different quarantine evidence.");
            }

            var effectiveTime = existingEvidence.FirstOrDefault()?.QuarantinedAtUtc
                ?? quarantinedAtUtc;
            foreach (var entry in unpublished.Where(static entry => entry.QuarantinedAtUtc is null))
            {
                entry.QuarantineReason = reason;
                entry.QuarantinedAtUtc = effectiveTime;
            }

            return ValueTask.CompletedTask;
        }
    }

    public ValueTask<IReadOnlyCollection<StationJobQuarantineItem>> ListQuarantinedAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_dispatchGate)
        {
            IReadOnlyCollection<StationJobQuarantineItem> result = _outbox.Values
                .Where(entry => entry.JobId == jobId && entry.QuarantinedAtUtc is not null)
                .OrderBy(static entry => entry.Sequence)
                .Select(static entry => new StationJobQuarantineItem(
                    entry.MessageId,
                    entry.JobId,
                    entry.Kind,
                    entry.Sequence,
                    entry.QuarantineReason!,
                    entry.QuarantinedAtUtc!.Value))
                .ToArray();
            return ValueTask.FromResult(result);
        }
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
            || existing.JobId != candidate.JobId
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

    private void RecordEventLocked(StationJobEventInboxItem item)
    {
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
    }

    private StationJobRequested RequireRequest(Guid jobId)
    {
        var entry = _outbox.Values.SingleOrDefault(item =>
            item.JobId == jobId
            && string.Equals(item.Kind, nameof(StationJobRequested), StringComparison.Ordinal));
        if (entry is null)
        {
            throw new InvalidDataException(
                $"Station result references unknown job {jobId:D}.");
        }

        return JsonSerializer.Deserialize<StationJobRequested>(entry.PayloadJson, JsonOptions)
            ?? throw new InvalidDataException("Station dispatch request payload is empty.");
    }

    private DateTimeOffset RequireAcceptedAndLatestEvent(StationJobRequested request)
    {
        var events = _events.Values.Where(item => item.JobId == request.JobId).ToArray();
        if (!events.Any(item => string.Equals(
                item.Kind,
                nameof(StationJobAccepted),
                StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "Station result arrived before durable acceptance evidence.");
        }

        return events.Select(static item => item.OccurredAtUtc)
            .Append(request.RequestedAtUtc)
            .Max();
    }

    private static void ValidateResultIdentity(
        StationJobRequested request,
        Guid jobId,
        string idempotencyKey,
        string agentId,
        string stationId)
    {
        if (request.JobId != jobId
            || !string.Equals(request.IdempotencyKey, idempotencyKey, StringComparison.Ordinal)
            || !string.Equals(request.AgentId, agentId, StringComparison.Ordinal)
            || !string.Equals(request.StationId, stationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Station result identity does not exactly match its dispatch request.");
        }
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
        Guid jobId,
        string idempotencyKey,
        string kind,
        int sequence,
        string payloadJson,
        DateTimeOffset createdAtUtc)
    {
        public Guid MessageId { get; } = messageId;

        public Guid JobId { get; } = jobId;

        public string IdempotencyKey { get; } = idempotencyKey;

        public string Kind { get; } = kind;

        public int Sequence { get; } = sequence;

        public string PayloadJson { get; } = payloadJson;

        public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;

        public int AttemptCount { get; set; }

        public string? LastError { get; set; }

        public bool Published { get; set; }

        public string? QuarantineReason { get; set; }

        public DateTimeOffset? QuarantinedAtUtc { get; set; }
    }
}
