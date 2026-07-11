using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Domain.StationJobs;

namespace OpenLineOps.Agent.Infrastructure.Persistence;

public sealed class InMemoryStationJobStore : IStationJobStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<StationJobId, StoredJob> _jobs = [];
    private readonly Dictionary<string, StationJobId> _idempotencyKeys =
        new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _inbox = [];
    private readonly Dictionary<Guid, StationJobOutboxMessage> _outbox = [];

    public ValueTask<StationJobPersistenceEntry?> GetAsync(
        StationJobId jobId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(
                _jobs.TryGetValue(jobId, out var stored)
                    ? ToEntry(stored)
                    : null);
        }
    }

    public ValueTask<StationJobPersistenceEntry?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(
                _idempotencyKeys.TryGetValue(idempotencyKey, out var jobId)
                && _jobs.TryGetValue(jobId, out var stored)
                    ? ToEntry(stored)
                    : null);
        }
    }

    public ValueTask<bool> TryAddAsync(
        StationJob job,
        Guid inboundMessageId,
        IReadOnlyCollection<StationJobOutboxMessage> outboxMessages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(outboxMessages);
        if (inboundMessageId == Guid.Empty)
        {
            throw new ArgumentException("Inbound message id cannot be empty.", nameof(inboundMessageId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_inbox.Contains(inboundMessageId)
                || _jobs.ContainsKey(job.Id)
                || _idempotencyKeys.ContainsKey(job.IdempotencyKey)
                || outboxMessages.Any(message => _outbox.ContainsKey(message.MessageId)))
            {
                return ValueTask.FromResult(false);
            }

            _inbox.Add(inboundMessageId);
            _jobs.Add(job.Id, new StoredJob(job.ToSnapshot(), 0));
            _idempotencyKeys.Add(job.IdempotencyKey, job.Id);
            foreach (var message in outboxMessages)
            {
                _outbox.Add(message.MessageId, message);
            }

            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<long> SaveAsync(
        StationJob job,
        long expectedRevision,
        IReadOnlyCollection<StationJobOutboxMessage> outboxMessages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(outboxMessages);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_jobs.TryGetValue(job.Id, out var stored)
                || stored.Revision != expectedRevision
                || outboxMessages.Any(message => _outbox.ContainsKey(message.MessageId)))
            {
                throw new StationJobConcurrencyException(job.Id, expectedRevision);
            }

            var nextRevision = checked(expectedRevision + 1);
            _jobs[job.Id] = new StoredJob(job.ToSnapshot(), nextRevision);
            foreach (var message in outboxMessages)
            {
                _outbox.Add(message.MessageId, message);
            }

            return ValueTask.FromResult(nextRevision);
        }
    }

    public ValueTask<IReadOnlyCollection<StationJobPersistenceEntry>> ListRecoverableAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var result = _jobs.Values
                .Where(stored => stored.Snapshot.Status is StationJobStatus.Accepted
                    or StationJobStatus.Running
                    or StationJobStatus.RecoveryRequired)
                .OrderBy(stored => stored.Snapshot.RequestedAtUtc)
                .ThenBy(stored => stored.Snapshot.JobId.Value)
                .Select(ToEntry)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyCollection<StationJobPersistenceEntry>>(result);
        }
    }

    public ValueTask<IReadOnlyCollection<StationJobOutboxMessage>> ListPendingOutboxAsync(
        int maximumCount,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var result = _outbox.Values
                .Where(message => message.AcknowledgedAtUtc is null
                    && (message.NextAttemptAtUtc is null || message.NextAttemptAtUtc <= nowUtc))
                .OrderBy(message => message.CreatedAtUtc)
                .ThenBy(message => message.JobId.Value)
                .ThenBy(message => message.Sequence)
                .ThenBy(message => message.MessageId)
                .Take(maximumCount)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyCollection<StationJobOutboxMessage>>(result);
        }
    }

    public ValueTask AcknowledgeOutboxAsync(
        Guid messageId,
        DateTimeOffset acknowledgedAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_outbox.TryGetValue(messageId, out var message))
            {
                throw new InvalidOperationException($"Station job outbox message {messageId:D} does not exist.");
            }

            _outbox[messageId] = message with { AcknowledgedAtUtc = acknowledgedAtUtc };
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask RecordOutboxFailureAsync(
        Guid messageId,
        DateTimeOffset retryAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_outbox.TryGetValue(messageId, out var message))
            {
                throw new InvalidOperationException($"Station job outbox message {messageId:D} does not exist.");
            }

            _outbox[messageId] = message with
            {
                AttemptCount = checked(message.AttemptCount + 1),
                NextAttemptAtUtc = retryAtUtc
            };
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask<IReadOnlyCollection<StationJobOutboxMessage>> ListPendingArtifactCleanupAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var result = _outbox.Values
                .Where(message => message.AcknowledgedAtUtc is not null
                    && string.Equals(
                        message.Kind,
                        StationAgentMessageKinds.JobCompletionPendingArtifactTransfer,
                        StringComparison.Ordinal))
                .OrderBy(message => message.AcknowledgedAtUtc)
                .ThenBy(message => message.MessageId)
                .Take(maximumCount)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyCollection<StationJobOutboxMessage>>(result);
        }
    }

    public ValueTask DeleteAcknowledgedOutboxAsync(
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_outbox.TryGetValue(messageId, out var message)
                || message.AcknowledgedAtUtc is null)
            {
                throw new InvalidOperationException(
                    $"Acknowledged Station job outbox message {messageId:D} does not exist.");
            }

            _outbox.Remove(messageId);
            return ValueTask.CompletedTask;
        }
    }

    private static StationJobPersistenceEntry ToEntry(StoredJob stored) => new(
        StationJob.Restore(stored.Snapshot).ToSnapshot(),
        stored.Revision);

    private sealed record StoredJob(StationJobSnapshot Snapshot, long Revision);
}
