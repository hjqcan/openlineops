using System.Collections.Concurrent;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Incidents;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Steps;

namespace OpenLineOps.Runtime.Application.Monitoring;

public sealed class RuntimeMonitoringProjection :
    IRuntimeMonitoringService,
    IRuntimeDomainEventSubscriber
{
    private readonly IRuntimeSessionRepository _sessionRepository;
    private readonly ConcurrentDictionary<RuntimeStationStatusKey, RuntimeStationStatusProjection> _stationStatuses =
        new(RuntimeStationStatusKeyComparer.Instance);
    private readonly ConcurrentDictionary<RuntimeTargetStatusKey, RuntimeTargetStatusState> _targetStatuses =
        new(RuntimeTargetStatusKeyComparer.Instance);
    private readonly ConcurrentDictionary<Guid, RuntimeTimelineEntry[]> _timelines = new();
    private readonly ConcurrentDictionary<Guid, RuntimeAlarmProjection> _alarms = new();
    private long _sequence;
    private long _targetStatusSequence;

    public RuntimeMonitoringProjection(IRuntimeSessionRepository sessionRepository)
    {
        _sessionRepository = sessionRepository;
    }

    public async ValueTask HandleAsync(
        IReadOnlyCollection<IDomainEvent> domainEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);

        var runtimeEvents = domainEvents
            .Select(domainEvent => new RuntimeEventEnvelope(domainEvent, TryGetSessionId(domainEvent)))
            .Where(envelope => envelope.SessionId is not null)
            .GroupBy(envelope => envelope.SessionId!.Value.Value);

        foreach (var eventGroup in runtimeEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sessionId = new RuntimeSessionId(eventGroup.Key);
            var session = await _sessionRepository
                .GetByIdAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);

            if (session is null)
            {
                continue;
            }

            var monitoringScope = ScopeFrom(session);
            var stationStatusKey = new RuntimeStationStatusKey(
                monitoringScope.ProjectId,
                monitoringScope.ApplicationId,
                monitoringScope.ProjectSnapshotId,
                monitoringScope.TopologyId,
                session.TraceMetadata.ProductionRunId,
                session.TraceMetadata.OperationId,
                session.TraceMetadata.StationSystemId);
            if (eventGroup.Any(envelope => envelope.DomainEvent is RuntimeSessionCreatedDomainEvent))
            {
                RemovePriorTargetOverlay(stationStatusKey);
            }

            _stationStatuses[stationStatusKey] = ToStationStatus(session);

            foreach (var envelope in eventGroup)
            {
                var timelineEntry = ToTimelineEntry(envelope.DomainEvent, session);
                AppendTimelineEntry(session.Id.Value, timelineEntry);

                if (envelope.DomainEvent is RuntimeIncidentRecordedDomainEvent incidentEvent)
                {
                    UpsertAlarm(session, incidentEvent);
                }

                if (envelope.DomainEvent is RuntimeCommandStatusChangedDomainEvent commandStatusEvent)
                {
                    UpsertTargetStatus(session, commandStatusEvent);
                }
            }
        }
    }

    public ValueTask<IReadOnlyCollection<RuntimeStationStatusProjection>> GetStationStatusesAsync(
        RuntimeMonitoringScope scope,
        string? stationSystemId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();

        var canonicalStationSystemId = OptionalCanonical(stationSystemId, nameof(stationSystemId));

        IReadOnlyCollection<RuntimeStationStatusProjection> statuses = _stationStatuses.Values
            .Where(status => MatchesScope(status, scope))
            .Where(status => canonicalStationSystemId is null
                || string.Equals(status.StationSystemId, canonicalStationSystemId, StringComparison.Ordinal))
            .OrderBy(status => status.StationSystemId, StringComparer.Ordinal)
            .ThenBy(status => status.ProductionRunId.Value)
            .ThenBy(status => status.OperationAttempt)
            .ThenBy(status => status.OperationId, StringComparer.Ordinal)
            .ToArray();

        return ValueTask.FromResult(statuses);
    }

    public ValueTask<IReadOnlyCollection<RuntimeTargetStatusProjection>> GetTargetStatusesAsync(
        RuntimeMonitoringScope scope,
        string? stationSystemId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();

        var canonicalStationSystemId = OptionalCanonical(stationSystemId, nameof(stationSystemId));

        IReadOnlyCollection<RuntimeTargetStatusProjection> statuses = _targetStatuses.Values
            .Select(state => state.Projection)
            .Where(status => MatchesScope(status, scope))
            .Where(status => canonicalStationSystemId is null
                || string.Equals(status.StationSystemId, canonicalStationSystemId, StringComparison.Ordinal))
            .OrderBy(status => status.StationSystemId, StringComparer.Ordinal)
            .ThenBy(status => status.ProductionRunId.Value)
            .ThenBy(status => status.OperationAttempt)
            .ThenBy(status => status.OperationId, StringComparer.Ordinal)
            .ThenBy(status => status.TargetKind, StringComparer.Ordinal)
            .ThenBy(status => status.TargetId, StringComparer.Ordinal)
            .ToArray();

        return ValueTask.FromResult(statuses);
    }

    public ValueTask<IReadOnlyCollection<RuntimeTimelineEntry>> GetSessionTimelineAsync(
        RuntimeSessionId sessionId,
        RuntimeMonitoringScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();

        var entries = _timelines.TryGetValue(sessionId.Value, out var timeline)
            ? timeline
                .Where(entry => MatchesScope(entry, scope))
                .OrderBy(entry => entry.Sequence)
                .ToArray()
            : [];

        return ValueTask.FromResult<IReadOnlyCollection<RuntimeTimelineEntry>>(entries);
    }

    public ValueTask<IReadOnlyCollection<RuntimeAlarmProjection>> GetAlarmsAsync(
        string? stationSystemId = null,
        bool includeAcknowledged = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var canonicalStationSystemId = OptionalCanonical(stationSystemId, nameof(stationSystemId));

        IReadOnlyCollection<RuntimeAlarmProjection> alarms = _alarms.Values
            .Where(alarm => includeAcknowledged || !alarm.IsAcknowledged)
            .Where(alarm => canonicalStationSystemId is null
                || string.Equals(alarm.StationSystemId, canonicalStationSystemId, StringComparison.Ordinal))
            .OrderByDescending(alarm => alarm.OccurredAtUtc)
            .ThenBy(alarm => alarm.AlarmId.Value)
            .ToArray();

        return ValueTask.FromResult(alarms);
    }

    public ValueTask<Result<RuntimeAlarmProjection>> AcknowledgeAlarmAsync(
        RuntimeIncidentId alarmId,
        string acknowledgedBy,
        DateTimeOffset acknowledgedAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(acknowledgedBy))
        {
            return ValueTask.FromResult(Result.Failure<RuntimeAlarmProjection>(
                ApplicationError.Validation(
                    "Runtime.AlarmAcknowledgedByRequired",
                    "AcknowledgedBy is required.")));
        }

        if (char.IsWhiteSpace(acknowledgedBy[0]) || char.IsWhiteSpace(acknowledgedBy[^1]))
        {
            return ValueTask.FromResult(Result.Failure<RuntimeAlarmProjection>(
                ApplicationError.Validation(
                    "Runtime.AlarmAcknowledgedByInvalid",
                    "AcknowledgedBy must be a canonical string.")));
        }

        while (true)
        {
            if (!_alarms.TryGetValue(alarmId.Value, out var alarm))
            {
                return ValueTask.FromResult(Result.Failure<RuntimeAlarmProjection>(
                    ApplicationError.NotFound(
                        "Runtime.AlarmNotFound",
                        $"Runtime alarm {alarmId.Value} was not found.")));
            }

            if (alarm.IsAcknowledged)
            {
                return ValueTask.FromResult(Result.Success(alarm));
            }

            var acknowledged = alarm.Acknowledge(acknowledgedBy, acknowledgedAtUtc);
            if (_alarms.TryUpdate(alarmId.Value, acknowledged, alarm))
            {
                return ValueTask.FromResult(Result.Success(acknowledged));
            }
        }
    }

    private void AppendTimelineEntry(Guid sessionId, RuntimeTimelineEntry entry)
    {
        _timelines.AddOrUpdate(
            sessionId,
            _ => [entry],
            (_, existing) => existing.Append(entry).OrderBy(item => item.Sequence).ToArray());
    }

    private void UpsertAlarm(RuntimeSession session, RuntimeIncidentRecordedDomainEvent incidentEvent)
    {
        var incident = session.Incidents.SingleOrDefault(candidate => candidate.Id == incidentEvent.IncidentId);
        if (incident is null)
        {
            return;
        }

        _alarms.AddOrUpdate(
            incident.Id.Value,
            _ => new RuntimeAlarmProjection(
                incident.Id,
                session.Id,
                session.StationId.Value,
                incident.Severity,
                incident.Code,
                incident.Message,
                incident.OccurredAtUtc,
                IsAcknowledged: false,
                AcknowledgedBy: null,
                AcknowledgedAtUtc: null),
            (_, existing) => existing);
    }

    private void UpsertTargetStatus(
        RuntimeSession session,
        RuntimeCommandStatusChangedDomainEvent commandStatusEvent)
    {
        var projection = RuntimeTargetStatusProjection.FromCommandStatusChanged(session, commandStatusEvent);
        var key = new RuntimeTargetStatusKey(
            projection.ProjectId,
            projection.ApplicationId,
            projection.ProjectSnapshotId,
            projection.TopologyId,
            projection.ProductionRunId,
            projection.OperationId,
            projection.StationSystemId,
            projection.TargetKind,
            projection.TargetId);
        var candidate = new RuntimeTargetStatusState(
            Interlocked.Increment(ref _targetStatusSequence),
            projection);

        _targetStatuses.AddOrUpdate(
            key,
            candidate,
            (_, current) => candidate.Sequence > current.Sequence ? candidate : current);
    }

    private RuntimeTimelineEntry ToTimelineEntry(IDomainEvent domainEvent, RuntimeSession session)
    {
        var sequence = Interlocked.Increment(ref _sequence);

        return domainEvent switch
        {
            RuntimeSessionCreatedDomainEvent sessionCreated => CreateEntry(
                sequence,
                sessionCreated,
                session,
                entityKind: "Session",
                entityId: sessionCreated.SessionId.Value.ToString("D")),
            RuntimeSessionStatusChangedDomainEvent sessionStatus => CreateEntry(
                sequence,
                sessionStatus,
                session,
                entityKind: "Session",
                entityId: sessionStatus.SessionId.Value.ToString("D"),
                fromStatus: sessionStatus.FromStatus.ToString(),
                toStatus: sessionStatus.ToStatus.ToString(),
                reason: sessionStatus.Reason),
            RuntimeStepStatusChangedDomainEvent stepStatus => CreateEntry(
                sequence,
                stepStatus,
                session,
                entityKind: "Step",
                entityId: stepStatus.StepId.Value.ToString("D"),
                fromStatus: stepStatus.FromStatus.ToString(),
                toStatus: stepStatus.ToStatus.ToString()),
            RuntimeCommandStatusChangedDomainEvent commandStatus => CreateEntry(
                sequence,
                commandStatus,
                session,
                entityKind: "Command",
                entityId: commandStatus.CommandId.Value.ToString("D"),
                fromStatus: commandStatus.FromStatus.ToString(),
                toStatus: commandStatus.ToStatus.ToString(),
                reason: commandStatus.Reason),
            RuntimeIncidentRecordedDomainEvent incidentRecorded => CreateEntry(
                sequence,
                incidentRecorded,
                session,
                entityKind: "Incident",
                entityId: incidentRecorded.IncidentId.Value.ToString("D"),
                severity: incidentRecorded.Severity.ToString(),
                code: incidentRecorded.Code),
            _ => CreateEntry(sequence, domainEvent, session, entityKind: "Unknown")
        };
    }

    private static RuntimeTimelineEntry CreateEntry(
        long sequence,
        IDomainEvent domainEvent,
        RuntimeSession session,
        string entityKind,
        string? entityId = null,
        string? fromStatus = null,
        string? toStatus = null,
        string? reason = null,
        string? severity = null,
        string? code = null)
    {
        return new RuntimeTimelineEntry(
            sequence,
            domainEvent.EventId,
            domainEvent.OccurredAtUtc,
            domainEvent.EventName,
            session.Id,
            session.TraceMetadata.ProjectId,
            session.TraceMetadata.ApplicationId,
            session.TraceMetadata.ProjectSnapshotId,
            session.TraceMetadata.TopologyId,
            session.TraceMetadata.ProductionRunId,
            session.TraceMetadata.ProductionLineDefinitionId,
            session.TraceMetadata.OperationId,
            session.TraceMetadata.OperationAttempt,
            session.TraceMetadata.StationSystemId,
            session.TraceMetadata.ProductionUnitIdentity,
            session.StationId.Value,
            entityKind,
            entityId,
            fromStatus,
            toStatus,
            reason,
            severity,
            code,
            session.Status);
    }

    private static RuntimeStationStatusProjection ToStationStatus(RuntimeSession session)
    {
        return new RuntimeStationStatusProjection(
            session.TraceMetadata.ProjectId,
            session.TraceMetadata.ApplicationId,
            session.TraceMetadata.ProjectSnapshotId,
            session.TraceMetadata.TopologyId,
            session.TraceMetadata.ProductionRunId,
            session.TraceMetadata.ProductionLineDefinitionId,
            session.TraceMetadata.OperationId,
            session.TraceMetadata.OperationAttempt,
            session.TraceMetadata.StationSystemId,
            session.TraceMetadata.ProductionUnitIdentity,
            session.StationId.Value,
            session.Id,
            session.ProcessDefinitionId.Value,
            session.ProcessVersionId.Value,
            session.ConfigurationSnapshotId.Value,
            session.RecipeSnapshotId.Value,
            session.TraceMetadata.LotId,
            session.TraceMetadata.CarrierId,
            session.TraceMetadata.FixtureId,
            session.TraceMetadata.DeviceId,
            session.Status,
            session.Steps.Count,
            session.Steps.Count(step => step.Status == RuntimeStepStatus.Completed),
            session.Steps.Count(step => step.Status == RuntimeStepStatus.Running),
            session.Commands.Count,
            session.Incidents.Count,
            session.LastTransitionAtUtc,
            session.IsTerminal);
    }

    private void RemovePriorTargetOverlay(RuntimeStationStatusKey stationStatusKey)
    {
        foreach (var entry in _targetStatuses)
        {
            if (RuntimeStationStatusKeyComparer.Matches(entry.Key, stationStatusKey))
            {
                _targetStatuses.TryRemove(entry.Key, out _);
            }
        }
    }

    private static RuntimeMonitoringScope ScopeFrom(RuntimeSession session)
    {
        return new RuntimeMonitoringScope(
            session.TraceMetadata.ProjectId,
            session.TraceMetadata.ApplicationId,
            session.TraceMetadata.ProjectSnapshotId,
            session.TraceMetadata.TopologyId,
            session.TraceMetadata.ProductionRunId);
    }

    private static bool MatchesScope(
        RuntimeStationStatusProjection status,
        RuntimeMonitoringScope scope)
    {
        return string.Equals(status.ProjectId, scope.ProjectId, StringComparison.Ordinal)
            && string.Equals(status.ApplicationId, scope.ApplicationId, StringComparison.Ordinal)
            && string.Equals(status.ProjectSnapshotId, scope.ProjectSnapshotId, StringComparison.Ordinal)
            && string.Equals(status.TopologyId, scope.TopologyId, StringComparison.Ordinal)
            && (scope.ProductionRunId is null
                || status.ProductionRunId == scope.ProductionRunId.Value);
    }

    private static bool MatchesScope(
        RuntimeTargetStatusProjection status,
        RuntimeMonitoringScope scope)
    {
        return string.Equals(status.ProjectId, scope.ProjectId, StringComparison.Ordinal)
            && string.Equals(status.ApplicationId, scope.ApplicationId, StringComparison.Ordinal)
            && string.Equals(status.ProjectSnapshotId, scope.ProjectSnapshotId, StringComparison.Ordinal)
            && string.Equals(status.TopologyId, scope.TopologyId, StringComparison.Ordinal)
            && (scope.ProductionRunId is null
                || status.ProductionRunId == scope.ProductionRunId.Value);
    }

    private static bool MatchesScope(
        RuntimeTimelineEntry entry,
        RuntimeMonitoringScope scope)
    {
        return string.Equals(entry.ProjectId, scope.ProjectId, StringComparison.Ordinal)
            && string.Equals(entry.ApplicationId, scope.ApplicationId, StringComparison.Ordinal)
            && string.Equals(entry.ProjectSnapshotId, scope.ProjectSnapshotId, StringComparison.Ordinal)
            && string.Equals(entry.TopologyId, scope.TopologyId, StringComparison.Ordinal)
            && (scope.ProductionRunId is null
                || entry.ProductionRunId == scope.ProductionRunId.Value);
    }

    private static string? OptionalCanonical(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1]))
        {
            throw new ArgumentException(
                $"{parameterName} must be null or a non-empty canonical string.",
                parameterName);
        }

        return value;
    }

    private static RuntimeSessionId? TryGetSessionId(IDomainEvent domainEvent)
    {
        return domainEvent switch
        {
            RuntimeSessionCreatedDomainEvent runtimeEvent => runtimeEvent.SessionId,
            RuntimeSessionStatusChangedDomainEvent runtimeEvent => runtimeEvent.SessionId,
            RuntimeStepStatusChangedDomainEvent runtimeEvent => runtimeEvent.SessionId,
            RuntimeCommandStatusChangedDomainEvent runtimeEvent => runtimeEvent.SessionId,
            RuntimeIncidentRecordedDomainEvent runtimeEvent => runtimeEvent.SessionId,
            _ => null
        };
    }

    private sealed record RuntimeEventEnvelope(IDomainEvent DomainEvent, RuntimeSessionId? SessionId);

    private readonly record struct RuntimeStationStatusKey(
        string ProjectId,
        string ApplicationId,
        string ProjectSnapshotId,
        string TopologyId,
        ProductionRunId ProductionRunId,
        string OperationId,
        string StationSystemId);

    private readonly record struct RuntimeTargetStatusKey(
        string ProjectId,
        string ApplicationId,
        string ProjectSnapshotId,
        string TopologyId,
        ProductionRunId ProductionRunId,
        string OperationId,
        string StationSystemId,
        string TargetKind,
        string TargetId);

    private sealed record RuntimeTargetStatusState(
        long Sequence,
        RuntimeTargetStatusProjection Projection);

    private sealed class RuntimeTargetStatusKeyComparer : IEqualityComparer<RuntimeTargetStatusKey>
    {
        public static RuntimeTargetStatusKeyComparer Instance { get; } = new();

        public bool Equals(RuntimeTargetStatusKey x, RuntimeTargetStatusKey y)
        {
            return StringComparer.Ordinal.Equals(x.ProjectId, y.ProjectId)
                && StringComparer.Ordinal.Equals(x.ApplicationId, y.ApplicationId)
                && StringComparer.Ordinal.Equals(x.ProjectSnapshotId, y.ProjectSnapshotId)
                && StringComparer.Ordinal.Equals(x.TopologyId, y.TopologyId)
                && x.ProductionRunId == y.ProductionRunId
                && StringComparer.Ordinal.Equals(x.OperationId, y.OperationId)
                && StringComparer.Ordinal.Equals(x.StationSystemId, y.StationSystemId)
                && StringComparer.Ordinal.Equals(x.TargetKind, y.TargetKind)
                && StringComparer.Ordinal.Equals(x.TargetId, y.TargetId);
        }

        public int GetHashCode(RuntimeTargetStatusKey obj)
        {
            var scopeHash = HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.ProjectId),
                StringComparer.Ordinal.GetHashCode(obj.ApplicationId),
                StringComparer.Ordinal.GetHashCode(obj.ProjectSnapshotId),
                StringComparer.Ordinal.GetHashCode(obj.TopologyId),
                obj.ProductionRunId,
                StringComparer.Ordinal.GetHashCode(obj.OperationId));
            return HashCode.Combine(
                scopeHash,
                StringComparer.Ordinal.GetHashCode(obj.StationSystemId),
                StringComparer.Ordinal.GetHashCode(obj.TargetKind),
                StringComparer.Ordinal.GetHashCode(obj.TargetId));
        }
    }

    private sealed class RuntimeStationStatusKeyComparer : IEqualityComparer<RuntimeStationStatusKey>
    {
        public static RuntimeStationStatusKeyComparer Instance { get; } = new();

        public bool Equals(RuntimeStationStatusKey x, RuntimeStationStatusKey y)
        {
            return StringComparer.Ordinal.Equals(x.ProjectId, y.ProjectId)
                && StringComparer.Ordinal.Equals(x.ApplicationId, y.ApplicationId)
                && StringComparer.Ordinal.Equals(x.ProjectSnapshotId, y.ProjectSnapshotId)
                && StringComparer.Ordinal.Equals(x.TopologyId, y.TopologyId)
                && x.ProductionRunId == y.ProductionRunId
                && StringComparer.Ordinal.Equals(x.OperationId, y.OperationId)
                && StringComparer.Ordinal.Equals(x.StationSystemId, y.StationSystemId);
        }

        public int GetHashCode(RuntimeStationStatusKey obj)
        {
            return HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.ProjectId),
                StringComparer.Ordinal.GetHashCode(obj.ApplicationId),
                StringComparer.Ordinal.GetHashCode(obj.ProjectSnapshotId),
                StringComparer.Ordinal.GetHashCode(obj.TopologyId),
                obj.ProductionRunId,
                StringComparer.Ordinal.GetHashCode(obj.OperationId),
                StringComparer.Ordinal.GetHashCode(obj.StationSystemId));
        }

        public static bool Matches(RuntimeTargetStatusKey target, RuntimeStationStatusKey station)
        {
            return StringComparer.Ordinal.Equals(target.ProjectId, station.ProjectId)
                && StringComparer.Ordinal.Equals(target.ApplicationId, station.ApplicationId)
                && StringComparer.Ordinal.Equals(target.ProjectSnapshotId, station.ProjectSnapshotId)
                && StringComparer.Ordinal.Equals(target.TopologyId, station.TopologyId)
                && target.ProductionRunId == station.ProductionRunId
                && StringComparer.Ordinal.Equals(target.OperationId, station.OperationId)
                && StringComparer.Ordinal.Equals(target.StationSystemId, station.StationSystemId);
        }
    }
}
