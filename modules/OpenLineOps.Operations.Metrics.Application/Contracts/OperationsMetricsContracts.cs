using OpenLineOps.Operations.Metrics.Domain.Downtime;
using OpenLineOps.Operations.Metrics.Domain.Metrics;
using OpenLineOps.Operations.Metrics.Domain.Production;
using OpenLineOps.Operations.Metrics.Domain.Shifts;

namespace OpenLineOps.Operations.Metrics.Application.Contracts;

public enum OperationsMetricsWriteOutcome
{
    Applied = 0,
    Replayed = 1
}

public sealed class OperationsMetricsConflictException(string message)
    : InvalidOperationException(message);

public sealed record RecordProductionEventCommand(
    string EventId,
    string StationId,
    string UnitId,
    ProductionEventKind Kind,
    DateTimeOffset SourceTimestampUtc,
    DateTimeOffset OccurredAtUtc,
    bool FirstAttempt,
    bool Good,
    TimeSpan? CycleDuration,
    int SchemaVersion);

public sealed record DefineShiftCommand(
    string ShiftId,
    string StationId,
    string Name,
    string TimeZoneId,
    TimeOnly LocalStartTime,
    TimeOnly LocalEndTime,
    string ActorId,
    int SchemaVersion);

public sealed record ScheduleProductionWindowCommand(
    string WindowId,
    string ShiftId,
    string StationId,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    int TargetQuantity,
    TimeSpan IdealCycleTime,
    string ActorId,
    int SchemaVersion);

public sealed record OpenDowntimeCommand(
    string FactId,
    string DowntimeId,
    string StationId,
    string SourceId,
    DateTimeOffset OccurredAtUtc,
    string ActorId);

public sealed record ClearDowntimeFromSourceCommand(
    string FactId,
    string DowntimeId,
    string StationId,
    string SourceId,
    DateTimeOffset OccurredAtUtc,
    string ActorId,
    int ExpectedRevision);

public sealed record AttributeDowntimeReasonCommand(
    string FactId,
    string DowntimeId,
    string ReasonCode,
    string? ReasonComment,
    DateTimeOffset OccurredAtUtc,
    string ActorId,
    int ExpectedRevision);

public sealed record OeeQuery(
    string StationId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc);

public sealed record OperationsMetricsWriteResult<T>(
    OperationsMetricsWriteOutcome Outcome,
    T Resource);

public sealed record ShiftSchedule(
    ShiftDefinition Definition,
    IReadOnlyList<PlannedProductionWindow> Windows);

public interface IOperationsMetricsService
{
    ValueTask<OperationsMetricsWriteResult<CanonicalProductionEvent>>
        RecordProductionEventAsync(
            RecordProductionEventCommand command,
            CancellationToken cancellationToken = default);

    ValueTask<OperationsMetricsWriteResult<ShiftDefinition>> DefineShiftAsync(
        DefineShiftCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<OperationsMetricsWriteResult<PlannedProductionWindow>>
        ScheduleProductionWindowAsync(
            ScheduleProductionWindowCommand command,
            CancellationToken cancellationToken = default);

    ValueTask<OperationsMetricsWriteResult<DowntimeInterval>> OpenDowntimeAsync(
        OpenDowntimeCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<OperationsMetricsWriteResult<DowntimeInterval>>
        ClearDowntimeFromSourceAsync(
            ClearDowntimeFromSourceCommand command,
            CancellationToken cancellationToken = default);

    ValueTask<OperationsMetricsWriteResult<DowntimeInterval>>
        AttributeDowntimeReasonAsync(
            AttributeDowntimeReasonCommand command,
            CancellationToken cancellationToken = default);

    ValueTask<DowntimeInterval?> GetDowntimeAsync(
        string downtimeId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<DowntimeInterval>> QueryDowntimeAsync(
        string stationId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ShiftSchedule>> QueryShiftsAsync(
        string stationId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default);

    ValueTask<OeeReport> CalculateOeeAsync(
        OeeQuery query,
        CancellationToken cancellationToken = default);
}
