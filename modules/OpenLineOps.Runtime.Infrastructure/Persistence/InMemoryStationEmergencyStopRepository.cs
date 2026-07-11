using OpenLineOps.Runtime.Application.Safety;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class InMemoryStationEmergencyStopRepository : IStationEmergencyStopRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, StationEmergencyStopRecord> _byIdempotency =
        new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> _idempotencyByMessageId = [];

    public ValueTask<StationEmergencyStopRegistration> RegisterRequestAsync(
        StationEmergencyStopRequestEvidence request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_byIdempotency.TryGetValue(request.IdempotencyKey, out var existing))
            {
                if (!StationSafetyCanonical.SameRequest(existing.Request, request))
                {
                    throw Conflict(request);
                }

                return ValueTask.FromResult(new StationEmergencyStopRegistration(
                    StationEmergencyStopRegistrationKind.Replay,
                    existing));
            }

            if (_idempotencyByMessageId.TryGetValue(request.MessageId, out var existingKey))
            {
                throw new StationEmergencyStopIdempotencyConflictException(
                    $"Emergency Stop Message ID {request.MessageId:D} already belongs to idempotency key '{existingKey}'.");
            }

            var created = StationEmergencyStopRecordTransitions.Create(request);
            _byIdempotency.Add(request.IdempotencyKey, created);
            _idempotencyByMessageId.Add(request.MessageId, request.IdempotencyKey);
            return ValueTask.FromResult(new StationEmergencyStopRegistration(
                StationEmergencyStopRegistrationKind.Created,
                created));
        }
    }

    public ValueTask<StationEmergencyStopRecord> RecordDispatchFailureAsync(
        string idempotencyKey,
        Guid requestMessageId,
        string failureCode,
        string failureReason,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var current = Required(idempotencyKey);
            var updated = StationEmergencyStopRecordTransitions.DispatchFailed(
                current,
                requestMessageId,
                failureCode,
                failureReason,
                failedAtUtc);
            _byIdempotency[idempotencyKey] = updated;
            return ValueTask.FromResult(updated);
        }
    }

    public ValueTask<StationEmergencyStopRecord> RecordAcknowledgementAsync(
        StationEmergencyStopAcknowledgementEvidence acknowledgement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var current = Required(acknowledgement.IdempotencyKey);
            var updated = StationEmergencyStopRecordTransitions.Acknowledge(
                current,
                acknowledgement);
            _byIdempotency[acknowledgement.IdempotencyKey] = updated;
            return ValueTask.FromResult(updated);
        }
    }

    public ValueTask<StationEmergencyStopRecord?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_byIdempotency.GetValueOrDefault(idempotencyKey));
        }
    }

    public ValueTask<IReadOnlyCollection<StationEmergencyStopRecord>> ListAsync(
        StationEmergencyStopQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyCollection<StationEmergencyStopRecord>>(
                _byIdempotency.Values
                    .Where(record => StationEmergencyStopRecordTransitions.Matches(record, query))
                    .OrderByDescending(static record => record.Request.RequestedAtUtc)
                    .ThenBy(static record => record.Request.MessageId)
                    .ToArray());
        }
    }

    private StationEmergencyStopRecord Required(string idempotencyKey) =>
        _byIdempotency.TryGetValue(idempotencyKey, out var record)
            ? record
            : throw new InvalidOperationException(
                $"Emergency Stop idempotency key '{idempotencyKey}' does not exist.");

    private static StationEmergencyStopIdempotencyConflictException Conflict(
        StationEmergencyStopRequestEvidence request) => new(
        $"Emergency Stop idempotency key '{request.IdempotencyKey}' was reused with different immutable evidence.");
}
