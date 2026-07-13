using System.Text.Json;
using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Application.Monitoring;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class InMemoryRuntimeSessionRepository :
    IRuntimeSessionRepository,
    IRuntimeMonitoringStore
{
    private static readonly JsonSerializerOptions JsonOptions = RuntimePersistenceJson.CreateOptions();

    private readonly object _gate = new();
    private readonly Dictionary<RuntimeSessionId, PersistedRuntimeSession> _sessions = [];
    private readonly List<StoredMonitoringEvent> _eventLog = [];
    private readonly HashSet<Guid> _projectedEventIds = [];
    private readonly Dictionary<StationStatusKey, RuntimeStationStatusProjection> _stationStatuses = [];
    private readonly Dictionary<TargetStatusKey, RuntimeTargetStatusProjection> _targetStatuses = [];
    private readonly Dictionary<Guid, RuntimeTimelineEntry> _timelineEntries = [];
    private readonly Dictionary<RuntimeIncidentId, RuntimeAlarmProjection> _alarms = [];
    private readonly Dictionary<RuntimeIncidentId, AlarmAcknowledgement> _acknowledgements = [];
    private long _nextSequence;
    private int _saveCount;

    public int SaveCount => Volatile.Read(ref _saveCount);

    public ValueTask SaveAsync(
        RuntimeSession session,
        IReadOnlyCollection<IDomainEvent> domainEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(domainEvents);
        cancellationToken.ThrowIfCancellationRequested();
        RequireRuntimeSessionEvents(session, domainEvents);

        var snapshot = RuntimeSessionSnapshotMapper.ToSnapshot(session);
        var snapshotDocument = JsonSerializer.Serialize(snapshot, JsonOptions);
        lock (_gate)
        {
            foreach (var domainEvent in domainEvents)
            {
                var existing = _eventLog.SingleOrDefault(item => item.DomainEvent.EventId == domainEvent.EventId);
                if (existing is not null)
                {
                    if (!Equals(existing.DomainEvent, domainEvent)
                        || !string.Equals(
                            existing.SessionDocument,
                            snapshotDocument,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Runtime monitoring event {domainEvent.EventId:D} conflicts with its durable record.");
                    }
                }
            }

            _sessions[session.Id] = snapshot;
            foreach (var domainEvent in domainEvents.Where(domainEvent =>
                         _eventLog.All(item => item.DomainEvent.EventId != domainEvent.EventId)))
            {
                _eventLog.Add(new StoredMonitoringEvent(
                    ++_nextSequence,
                    snapshotDocument,
                    domainEvent));
            }

            Interlocked.Increment(ref _saveCount);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<RuntimeSession?> GetByIdAsync(
        RuntimeSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(
                _sessions.TryGetValue(sessionId, out var snapshot)
                    ? RuntimeSessionSnapshotMapper.ToAggregate(snapshot)
                    : null);
        }
    }

    public ValueTask<IReadOnlyCollection<RuntimeSession>> ListRecoverableAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyCollection<RuntimeSession> sessions = _sessions.Values
                .Select(RuntimeSessionSnapshotMapper.ToAggregate)
                .Where(session => !session.IsTerminal)
                .OrderBy(session => session.LastTransitionAtUtc)
                .ThenBy(session => session.Id.Value)
                .ToArray();
            return ValueTask.FromResult(sessions);
        }
    }

    public ValueTask RebuildAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _stationStatuses.Clear();
            _targetStatuses.Clear();
            _timelineEntries.Clear();
            _alarms.Clear();
            _projectedEventIds.Clear();
            foreach (var storedEvent in _eventLog.OrderBy(item => item.Sequence))
            {
                Apply(storedEvent);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ApplyPendingAsync(
        IReadOnlyCollection<Guid> requiredEventIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requiredEventIds);
        cancellationToken.ThrowIfCancellationRequested();
        if (requiredEventIds.Any(eventId => eventId == Guid.Empty))
        {
            throw new ArgumentException("Required Runtime monitoring event ids cannot be empty.", nameof(requiredEventIds));
        }

        lock (_gate)
        {
            var persistedEventIds = _eventLog.Select(item => item.DomainEvent.EventId).ToHashSet();
            var missingEventId = requiredEventIds.FirstOrDefault(eventId => !persistedEventIds.Contains(eventId));
            if (missingEventId != Guid.Empty)
            {
                throw new InvalidOperationException(
                    $"Runtime monitoring event {missingEventId:D} was published before durable session persistence.");
            }

            foreach (var storedEvent in _eventLog
                         .Where(item => !_projectedEventIds.Contains(item.DomainEvent.EventId))
                         .OrderBy(item => item.Sequence))
            {
                Apply(storedEvent);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyCollection<RuntimeStationStatusProjection>> ListStationStatusesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyCollection<RuntimeStationStatusProjection>>(
                _stationStatuses.Values.ToArray());
        }
    }

    public ValueTask<IReadOnlyCollection<RuntimeTargetStatusProjection>> ListTargetStatusesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyCollection<RuntimeTargetStatusProjection>>(
                _targetStatuses.Values.ToArray());
        }
    }

    public ValueTask<IReadOnlyCollection<RuntimeTimelineEntry>> ListTimelineAsync(
        RuntimeSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyCollection<RuntimeTimelineEntry> entries = _timelineEntries.Values
                .Where(entry => entry.SessionId == sessionId)
                .OrderBy(entry => entry.Sequence)
                .ToArray();
            return ValueTask.FromResult(entries);
        }
    }

    public ValueTask<IReadOnlyCollection<RuntimeAlarmProjection>> ListAlarmsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyCollection<RuntimeAlarmProjection> alarms = _alarms.Values
                .Select(ApplyAcknowledgement)
                .ToArray();
            return ValueTask.FromResult(alarms);
        }
    }

    public ValueTask<RuntimeAlarmProjection?> AcknowledgeAlarmAsync(
        RuntimeIncidentId alarmId,
        string acknowledgedBy,
        DateTimeOffset acknowledgedAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_alarms.TryGetValue(alarmId, out var alarm))
            {
                return ValueTask.FromResult<RuntimeAlarmProjection?>(null);
            }

            if (!_acknowledgements.TryGetValue(alarmId, out var acknowledgement))
            {
                acknowledgement = new AlarmAcknowledgement(acknowledgedBy, acknowledgedAtUtc);
                _acknowledgements.Add(alarmId, acknowledgement);
            }

            return ValueTask.FromResult<RuntimeAlarmProjection?>(
                alarm.Acknowledge(acknowledgement.AcknowledgedBy, acknowledgement.AcknowledgedAtUtc));
        }
    }

    private void Apply(StoredMonitoringEvent storedEvent)
    {
        if (_projectedEventIds.Contains(storedEvent.DomainEvent.EventId))
        {
            return;
        }

        var sessionSnapshot = JsonSerializer.Deserialize<PersistedRuntimeSession>(
                storedEvent.SessionDocument,
                JsonOptions)
            ?? throw new InvalidOperationException(
                $"In-memory Runtime monitoring event {storedEvent.DomainEvent.EventId:D} has no session snapshot.");
        var session = RuntimeSessionSnapshotMapper.ToAggregate(sessionSnapshot);
        var projection = RuntimeMonitoringEventProjection.Create(
            storedEvent.Sequence,
            session,
            storedEvent.DomainEvent);
        _projectedEventIds.Add(storedEvent.DomainEvent.EventId);
        var stationKey = StationStatusKey.From(projection.StationStatus);
        if (projection.ResetStationTargets)
        {
            foreach (var targetKey in _targetStatuses.Keys
                         .Where(candidate => candidate.Matches(stationKey))
                         .ToArray())
            {
                _targetStatuses.Remove(targetKey);
            }
        }

        _stationStatuses[stationKey] = projection.StationStatus;
        _timelineEntries[projection.TimelineEntry.EventId] = projection.TimelineEntry;
        if (projection.TargetStatus is not null)
        {
            _targetStatuses[TargetStatusKey.From(projection.TargetStatus)] = projection.TargetStatus;
        }

        if (projection.Alarm is not null)
        {
            if (_alarms.TryGetValue(projection.Alarm.AlarmId, out var existingAlarm)
                && existingAlarm != projection.Alarm)
            {
                throw new InvalidOperationException(
                    $"Runtime alarm {projection.Alarm.AlarmId} conflicts with its projected record.");
            }

            _alarms.TryAdd(projection.Alarm.AlarmId, projection.Alarm);
        }
    }

    private RuntimeAlarmProjection ApplyAcknowledgement(RuntimeAlarmProjection alarm)
    {
        return _acknowledgements.TryGetValue(alarm.AlarmId, out var acknowledgement)
            ? alarm.Acknowledge(acknowledgement.AcknowledgedBy, acknowledgement.AcknowledgedAtUtc)
            : alarm;
    }

    private static void RequireRuntimeSessionEvents(
        RuntimeSession session,
        IReadOnlyCollection<IDomainEvent> domainEvents)
    {
        var pendingEventIds = session.DomainEvents.Select(domainEvent => domainEvent.EventId).ToArray();
        if (!pendingEventIds.SequenceEqual(domainEvents.Select(domainEvent => domainEvent.EventId)))
        {
            throw new InvalidOperationException(
                "Runtime session persistence must atomically include every pending Domain Event in order.");
        }

        if (domainEvents.Select(domainEvent => domainEvent.EventId).Distinct().Count() != domainEvents.Count)
        {
            throw new InvalidOperationException("Runtime session persistence contains duplicate Domain Event ids.");
        }

        foreach (var domainEvent in domainEvents)
        {
            _ = RuntimeMonitoringEventProjection.Create(1, session, domainEvent);
        }
    }

    private sealed record StoredMonitoringEvent(
        long Sequence,
        string SessionDocument,
        IDomainEvent DomainEvent);

    private sealed record AlarmAcknowledgement(
        string AcknowledgedBy,
        DateTimeOffset AcknowledgedAtUtc);

    private readonly record struct StationStatusKey(
        string ProjectId,
        string ApplicationId,
        string ProjectSnapshotId,
        string TopologyId,
        Guid ProductionRunId,
        string OperationId,
        string StationSystemId)
    {
        public static StationStatusKey From(RuntimeStationStatusProjection status)
        {
            return new StationStatusKey(
                status.ProjectId,
                status.ApplicationId,
                status.ProjectSnapshotId,
                status.TopologyId,
                status.ProductionRunId.Value,
                status.OperationId,
                status.StationSystemId);
        }
    }

    private readonly record struct TargetStatusKey(
        StationStatusKey Station,
        string TargetKind,
        string TargetId)
    {
        public static TargetStatusKey From(RuntimeTargetStatusProjection status)
        {
            return new TargetStatusKey(
                new StationStatusKey(
                    status.ProjectId,
                    status.ApplicationId,
                    status.ProjectSnapshotId,
                    status.TopologyId,
                    status.ProductionRunId.Value,
                    status.OperationId,
                    status.StationSystemId),
                status.TargetKind,
                status.TargetId);
        }

        public bool Matches(StationStatusKey station) => Station == station;
    }
}
