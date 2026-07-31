using OpenLineOps.Operations.Metrics.Application.Contracts;
using OpenLineOps.Operations.Metrics.Application.Metrics;
using OpenLineOps.Operations.Metrics.Application.Persistence;
using OpenLineOps.Operations.Metrics.Domain.Downtime;
using OpenLineOps.Operations.Metrics.Domain.Metrics;
using OpenLineOps.Operations.Metrics.Domain.Production;
using OpenLineOps.Operations.Metrics.Domain.Shifts;

namespace OpenLineOps.Operations.Metrics.Application.Services;

public sealed class OperationsMetricsService(
    IOperationsMetricsStore store,
    TimeProvider timeProvider) : IOperationsMetricsService
{
    public async ValueTask<OperationsMetricsWriteResult<CanonicalProductionEvent>>
        RecordProductionEventAsync(
            RecordProductionEventCommand command,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var productionEvent = new CanonicalProductionEvent(
            command.EventId,
            command.StationId,
            command.UnitId,
            command.Kind,
            command.SourceTimestampUtc,
            command.OccurredAtUtc,
            timeProvider.GetUtcNow(),
            command.FirstAttempt,
            command.Good,
            command.CycleDuration,
            command.SchemaVersion);
        var outcome = await store
            .AppendProductionEventAsync(productionEvent, cancellationToken)
            .ConfigureAwait(false);
        var persisted = outcome == OperationsMetricsWriteOutcome.Replayed
            ? await store
                .GetProductionEventAsync(command.EventId, cancellationToken)
                .ConfigureAwait(false)
            : productionEvent;
        return new OperationsMetricsWriteResult<CanonicalProductionEvent>(
            outcome,
            persisted
            ?? throw new InvalidDataException(
                "The replayed production event could not be reloaded."));
    }

    public async ValueTask<OperationsMetricsWriteResult<ShiftDefinition>>
        DefineShiftAsync(
            DefineShiftCommand command,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var shift = new ShiftDefinition(
            command.ShiftId,
            command.StationId,
            command.Name,
            command.TimeZoneId,
            command.LocalStartTime,
            command.LocalEndTime,
            command.ActorId,
            timeProvider.GetUtcNow(),
            command.SchemaVersion);
        var outcome = await store
            .AppendShiftDefinitionAsync(shift, cancellationToken)
            .ConfigureAwait(false);
        var persisted = outcome == OperationsMetricsWriteOutcome.Replayed
            ? await store
                .GetShiftAsync(command.ShiftId, cancellationToken)
                .ConfigureAwait(false)
            : shift;
        return new OperationsMetricsWriteResult<ShiftDefinition>(
            outcome,
            persisted
            ?? throw new InvalidDataException(
                "The replayed shift definition could not be reloaded."));
    }

    public async ValueTask<OperationsMetricsWriteResult<PlannedProductionWindow>>
        ScheduleProductionWindowAsync(
            ScheduleProductionWindowCommand command,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var shift = await store
            .GetShiftAsync(command.ShiftId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException(
                $"Shift '{command.ShiftId}' was not found.");
        if (!string.Equals(
                shift.StationId,
                command.StationId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A planned production window must belong to its shift station.");
        }

        var window = new PlannedProductionWindow(
            command.WindowId,
            command.ShiftId,
            command.StationId,
            command.StartsAtUtc,
            command.EndsAtUtc,
            command.TargetQuantity,
            command.IdealCycleTime,
            command.ActorId,
            timeProvider.GetUtcNow(),
            command.SchemaVersion);
        var outcome = await store
            .AppendProductionWindowAsync(window, cancellationToken)
            .ConfigureAwait(false);
        var persisted = outcome == OperationsMetricsWriteOutcome.Replayed
            ? await store
                .GetProductionWindowAsync(command.WindowId, cancellationToken)
                .ConfigureAwait(false)
            : window;
        return new OperationsMetricsWriteResult<PlannedProductionWindow>(
            outcome,
            persisted
            ?? throw new InvalidDataException(
                "The replayed production window could not be reloaded."));
    }

    public async ValueTask<OperationsMetricsWriteResult<DowntimeInterval>>
        OpenDowntimeAsync(
            OpenDowntimeCommand command,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var interval = DowntimeInterval.Open(
            command.FactId,
            command.DowntimeId,
            command.StationId,
            command.SourceId,
            command.OccurredAtUtc,
            command.ActorId);
        var outcome = await store
            .AppendDowntimeFactAsync(
                interval.Facts[0],
                expectedRevision: 0,
                cancellationToken)
            .ConfigureAwait(false);
        var persisted = await store
            .GetDowntimeAsync(command.DowntimeId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                "The appended downtime interval could not be reloaded.");
        return new OperationsMetricsWriteResult<DowntimeInterval>(
            outcome,
            persisted);
    }

    public async ValueTask<OperationsMetricsWriteResult<DowntimeInterval>>
        ClearDowntimeFromSourceAsync(
            ClearDowntimeFromSourceCommand command,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var interval = await GetRequiredDowntimeAsync(
                command.DowntimeId,
                cancellationToken)
            .ConfigureAwait(false);
        var candidateRevision = GetNextRevision(command.ExpectedRevision);
        var candidate = new DowntimeFact(
            command.FactId,
            command.DowntimeId,
            candidateRevision,
            DowntimeFactKind.SourceCleared,
            command.StationId,
            command.SourceId,
            command.OccurredAtUtc,
            command.ActorId);
        var replay = await TryReplayDowntimeFactAsync(
                interval,
                candidate,
                command.ExpectedRevision,
                cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
        {
            return replay;
        }

        var fact = interval.ClearFromSource(
            command.FactId,
            command.StationId,
            command.SourceId,
            command.OccurredAtUtc,
            command.ActorId);
        return await AppendAndReloadDowntimeAsync(
                fact,
                command.ExpectedRevision,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<OperationsMetricsWriteResult<DowntimeInterval>>
        AttributeDowntimeReasonAsync(
            AttributeDowntimeReasonCommand command,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var interval = await GetRequiredDowntimeAsync(
                command.DowntimeId,
                cancellationToken)
            .ConfigureAwait(false);
        var candidateRevision = GetNextRevision(command.ExpectedRevision);
        var candidate = new DowntimeFact(
            command.FactId,
            command.DowntimeId,
            candidateRevision,
            DowntimeFactKind.ReasonAttributed,
            interval.StationId,
            interval.SourceId,
            command.OccurredAtUtc,
            command.ActorId,
            command.ReasonCode,
            command.ReasonComment);
        var replay = await TryReplayDowntimeFactAsync(
                interval,
                candidate,
                command.ExpectedRevision,
                cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
        {
            return replay;
        }

        var fact = interval.AttributeReason(
            command.FactId,
            command.ReasonCode,
            command.ReasonComment,
            command.OccurredAtUtc,
            command.ActorId);
        return await AppendAndReloadDowntimeAsync(
                fact,
                command.ExpectedRevision,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<DowntimeInterval?> GetDowntimeAsync(
        string downtimeId,
        CancellationToken cancellationToken = default) =>
        store.GetDowntimeAsync(downtimeId, cancellationToken);

    public ValueTask<IReadOnlyList<DowntimeInterval>> QueryDowntimeAsync(
        string stationId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateQuery(stationId, fromUtc, toUtc);
        return store.QueryDowntimeAsync(
            stationId,
            fromUtc,
            toUtc,
            cancellationToken);
    }

    public async ValueTask<IReadOnlyList<ShiftSchedule>> QueryShiftsAsync(
        string stationId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateQuery(stationId, fromUtc, toUtc);
        var definitions = await store
            .QueryShiftDefinitionsAsync(stationId, cancellationToken)
            .ConfigureAwait(false);
        var windows = await store
            .QueryProductionWindowsAsync(
                stationId,
                fromUtc,
                toUtc,
                cancellationToken)
            .ConfigureAwait(false);
        return definitions
            .Select(definition => new ShiftSchedule(
                definition,
                windows
                    .Where(window => string.Equals(
                        window.ShiftId,
                        definition.ShiftId,
                        StringComparison.Ordinal))
                    .OrderBy(static window => window.StartsAtUtc)
                    .ToArray()))
            .ToArray();
    }

    public async ValueTask<OeeReport> CalculateOeeAsync(
        OeeQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateQuery(query.StationId, query.FromUtc, query.ToUtc);
        var windowsTask = store.QueryProductionWindowsAsync(
            query.StationId,
            query.FromUtc,
            query.ToUtc,
            cancellationToken);
        var eventsTask = store.QueryProductionEventsAsync(
            query.StationId,
            query.FromUtc,
            query.ToUtc,
            cancellationToken);
        var downtimeTask = store.QueryDowntimeAsync(
            query.StationId,
            query.FromUtc,
            query.ToUtc,
            cancellationToken);
        var windows = await windowsTask.ConfigureAwait(false);
        var productionEvents = await eventsTask.ConfigureAwait(false);
        var downtime = await downtimeTask.ConfigureAwait(false);
        return OeeCalculator.Calculate(
            query.StationId,
            query.FromUtc,
            query.ToUtc,
            windows,
            productionEvents,
            downtime);
    }

    private async ValueTask<DowntimeInterval> GetRequiredDowntimeAsync(
        string downtimeId,
        CancellationToken cancellationToken)
    {
        return await store
                   .GetDowntimeAsync(downtimeId, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new KeyNotFoundException(
                   $"Downtime interval '{downtimeId}' was not found.");
    }

    private async ValueTask<OperationsMetricsWriteResult<DowntimeInterval>>
        AppendAndReloadDowntimeAsync(
            DowntimeFact fact,
            int expectedRevision,
            CancellationToken cancellationToken)
    {
        var outcome = await store
            .AppendDowntimeFactAsync(
                fact,
                expectedRevision,
                cancellationToken)
            .ConfigureAwait(false);
        var persisted = await GetRequiredDowntimeAsync(
                fact.DowntimeId,
                cancellationToken)
            .ConfigureAwait(false);
        return new OperationsMetricsWriteResult<DowntimeInterval>(
            outcome,
            persisted);
    }

    private async ValueTask<OperationsMetricsWriteResult<DowntimeInterval>?>
        TryReplayDowntimeFactAsync(
            DowntimeInterval interval,
            DowntimeFact candidate,
            int expectedRevision,
            CancellationToken cancellationToken)
    {
        if (expectedRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision),
                expectedRevision,
                "Expected revision must be positive.");
        }

        if (interval.Revision == expectedRevision)
        {
            return null;
        }

        var persisted = await store
            .GetDowntimeFactAsync(candidate.FactId, cancellationToken)
            .ConfigureAwait(false);
        if (persisted == candidate)
        {
            return new OperationsMetricsWriteResult<DowntimeInterval>(
                OperationsMetricsWriteOutcome.Replayed,
                interval);
        }

        throw new OperationsMetricsConflictException(
            $"Downtime revision conflict. Expected {expectedRevision}, actual {interval.Revision}.");
    }

    private static int GetNextRevision(int expectedRevision)
    {
        return expectedRevision <= 0 || expectedRevision == int.MaxValue
            ? throw new ArgumentOutOfRangeException(
                nameof(expectedRevision),
                expectedRevision,
                "Expected revision must be positive and incrementable.")
            : expectedRevision + 1;
    }

    private static void ValidateQuery(
        string stationId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        if (!string.Equals(stationId, stationId.Trim(), StringComparison.Ordinal)
            || fromUtc == default
            || toUtc == default
            || fromUtc.Offset != TimeSpan.Zero
            || toUtc.Offset != TimeSpan.Zero
            || toUtc <= fromUtc)
        {
            throw new ArgumentException(
                "A query requires a canonical station ID and a non-empty UTC half-open interval.");
        }
    }
}
