using OpenLineOps.Operations.Metrics.Application.Contracts;
using OpenLineOps.Operations.Metrics.Domain.Downtime;
using OpenLineOps.Operations.Metrics.Domain.Production;
using OpenLineOps.Operations.Metrics.Domain.Shifts;

namespace OpenLineOps.Operations.Metrics.Application.Persistence;

public interface IOperationsMetricsStore
{
    ValueTask<OperationsMetricsWriteOutcome> AppendProductionEventAsync(
        CanonicalProductionEvent productionEvent,
        CancellationToken cancellationToken = default);

    ValueTask<OperationsMetricsWriteOutcome> AppendShiftDefinitionAsync(
        ShiftDefinition shift,
        CancellationToken cancellationToken = default);

    ValueTask<OperationsMetricsWriteOutcome> AppendProductionWindowAsync(
        PlannedProductionWindow window,
        CancellationToken cancellationToken = default);

    ValueTask<OperationsMetricsWriteOutcome> AppendDowntimeFactAsync(
        DowntimeFact fact,
        int expectedRevision,
        CancellationToken cancellationToken = default);

    ValueTask<ShiftDefinition?> GetShiftAsync(
        string shiftId,
        CancellationToken cancellationToken = default);

    ValueTask<CanonicalProductionEvent?> GetProductionEventAsync(
        string eventId,
        CancellationToken cancellationToken = default);

    ValueTask<PlannedProductionWindow?> GetProductionWindowAsync(
        string windowId,
        CancellationToken cancellationToken = default);

    ValueTask<DowntimeInterval?> GetDowntimeAsync(
        string downtimeId,
        CancellationToken cancellationToken = default);

    ValueTask<DowntimeFact?> GetDowntimeFactAsync(
        string factId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<CanonicalProductionEvent>>
        QueryProductionEventsAsync(
            string stationId,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<PlannedProductionWindow>>
        QueryProductionWindowsAsync(
            string stationId,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ShiftDefinition>> QueryShiftDefinitionsAsync(
        string stationId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<DowntimeInterval>> QueryDowntimeAsync(
        string stationId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default);
}
