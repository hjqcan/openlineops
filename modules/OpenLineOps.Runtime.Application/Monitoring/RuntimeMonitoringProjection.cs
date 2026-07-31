using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Application.Monitoring;

public sealed class RuntimeMonitoringProjection :
    IRuntimeMonitoringService,
    IRuntimeMonitoringProjectionInitializer,
    IRuntimeDomainEventSubscriber,
    IDisposable
{
    private readonly IRuntimeMonitoringStore _store;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private int _initialized;

    public RuntimeMonitoringProjection(IRuntimeMonitoringStore store)
    {
        _store = store;
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _initialized) == 1)
        {
            return;
        }

        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _initialized) == 1)
            {
                return;
            }

            await _store.RebuildAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _initialized, 1);
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async ValueTask HandleAsync(
        IReadOnlyCollection<IDomainEvent> domainEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);
        var runtimeEventIds = domainEvents
            .Where(RuntimeMonitoringEventProjection.IsRuntimeSessionEvent)
            .Select(domainEvent => domainEvent.EventId)
            .Distinct()
            .ToArray();
        if (runtimeEventIds.Length == 0)
        {
            return;
        }

        EnsureInitialized();
        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.ApplyPendingAsync(runtimeEventIds, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _applyGate.Release();
        }
    }

    public async ValueTask<IReadOnlyCollection<RuntimeStationStatusProjection>> GetStationStatusesAsync(
        RuntimeMonitoringScope scope,
        string? stationSystemId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        EnsureInitialized();
        var canonicalStationSystemId = OptionalCanonical(stationSystemId, nameof(stationSystemId));
        var statuses = await _store.ListStationStatusesAsync(cancellationToken).ConfigureAwait(false);

        return statuses
            .Where(status => MatchesScope(status, scope))
            .Where(status => canonicalStationSystemId is null
                || string.Equals(status.StationSystemId, canonicalStationSystemId, StringComparison.Ordinal))
            .OrderBy(status => status.StationSystemId, StringComparer.Ordinal)
            .ThenBy(status => status.ProductionRunId.Value)
            .ThenBy(status => status.OperationAttempt)
            .ThenBy(status => status.OperationId, StringComparer.Ordinal)
            .ToArray();
    }

    public async ValueTask<IReadOnlyCollection<RuntimeTargetStatusProjection>> GetTargetStatusesAsync(
        RuntimeMonitoringScope scope,
        string? stationSystemId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        EnsureInitialized();
        var canonicalStationSystemId = OptionalCanonical(stationSystemId, nameof(stationSystemId));
        var statuses = await _store.ListTargetStatusesAsync(cancellationToken).ConfigureAwait(false);

        return statuses
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
    }

    public async ValueTask<IReadOnlyCollection<RuntimeTimelineEntry>> GetSessionTimelineAsync(
        RuntimeSessionId sessionId,
        RuntimeMonitoringScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        EnsureInitialized();
        var entries = await _store.ListTimelineAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return entries
            .Where(entry => MatchesScope(entry, scope))
            .OrderBy(entry => entry.Sequence)
            .ToArray();
    }

    public async ValueTask<IReadOnlyCollection<RuntimeAlarmProjection>> GetAlarmsAsync(
        string? stationSystemId = null,
        bool includeAcknowledged = false,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var canonicalStationSystemId = OptionalCanonical(stationSystemId, nameof(stationSystemId));
        var alarms = await _store.ListAlarmsAsync(cancellationToken).ConfigureAwait(false);
        return alarms
            .Where(alarm => includeAcknowledged || !alarm.IsAcknowledged)
            .Where(alarm => canonicalStationSystemId is null
                || string.Equals(alarm.StationSystemId, canonicalStationSystemId, StringComparison.Ordinal))
            .OrderByDescending(alarm => alarm.OccurredAtUtc)
            .ThenBy(alarm => alarm.AlarmId.Value)
            .ToArray();
    }

    public async ValueTask<Result<RuntimeAlarmProjection>> AcknowledgeAlarmAsync(
        RuntimeIncidentId alarmId,
        string acknowledgedBy,
        DateTimeOffset acknowledgedAtUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (string.IsNullOrWhiteSpace(acknowledgedBy))
        {
            return Result.Failure<RuntimeAlarmProjection>(ApplicationError.Validation(
                "Runtime.AlarmAcknowledgedByRequired",
                "AcknowledgedBy is required."));
        }

        if (char.IsWhiteSpace(acknowledgedBy[0]) || char.IsWhiteSpace(acknowledgedBy[^1]))
        {
            return Result.Failure<RuntimeAlarmProjection>(ApplicationError.Validation(
                "Runtime.AlarmAcknowledgedByInvalid",
                "AcknowledgedBy must be a canonical string."));
        }

        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        RuntimeAlarmProjection? alarm;
        try
        {
            alarm = await _store
                .AcknowledgeAlarmAsync(alarmId, acknowledgedBy, acknowledgedAtUtc, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _applyGate.Release();
        }

        return alarm is null
            ? Result.Failure<RuntimeAlarmProjection>(ApplicationError.NotFound(
                "Runtime.AlarmNotFound",
                $"Runtime alarm {alarmId.Value} was not found."))
            : Result.Success(alarm);
    }

    private void EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized) != 1)
        {
            throw new InvalidOperationException(
                "Runtime monitoring projection has not completed its durable rebuild.");
        }
    }

    private static bool MatchesScope(RuntimeStationStatusProjection status, RuntimeMonitoringScope scope)
    {
        return string.Equals(status.ProjectId, scope.ProjectId, StringComparison.Ordinal)
            && string.Equals(status.ApplicationId, scope.ApplicationId, StringComparison.Ordinal)
            && string.Equals(status.ProjectSnapshotId, scope.ProjectSnapshotId, StringComparison.Ordinal)
            && string.Equals(status.TopologyId, scope.TopologyId, StringComparison.Ordinal)
            && (scope.ProductionRunId is null || status.ProductionRunId == scope.ProductionRunId.Value);
    }

    private static bool MatchesScope(RuntimeTargetStatusProjection status, RuntimeMonitoringScope scope)
    {
        return string.Equals(status.ProjectId, scope.ProjectId, StringComparison.Ordinal)
            && string.Equals(status.ApplicationId, scope.ApplicationId, StringComparison.Ordinal)
            && string.Equals(status.ProjectSnapshotId, scope.ProjectSnapshotId, StringComparison.Ordinal)
            && string.Equals(status.TopologyId, scope.TopologyId, StringComparison.Ordinal)
            && (scope.ProductionRunId is null || status.ProductionRunId == scope.ProductionRunId.Value);
    }

    private static bool MatchesScope(RuntimeTimelineEntry entry, RuntimeMonitoringScope scope)
    {
        return string.Equals(entry.ProjectId, scope.ProjectId, StringComparison.Ordinal)
            && string.Equals(entry.ApplicationId, scope.ApplicationId, StringComparison.Ordinal)
            && string.Equals(entry.ProjectSnapshotId, scope.ProjectSnapshotId, StringComparison.Ordinal)
            && string.Equals(entry.TopologyId, scope.TopologyId, StringComparison.Ordinal)
            && (scope.ProductionRunId is null || entry.ProductionRunId == scope.ProductionRunId.Value);
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

    public void Dispose()
    {
        _initializeGate.Dispose();
        _applyGate.Dispose();
    }
}
