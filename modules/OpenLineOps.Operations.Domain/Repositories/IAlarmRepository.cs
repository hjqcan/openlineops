using OpenLineOps.Domain.Abstractions.Repositories;
using OpenLineOps.Operations.Domain.Aggregates;
using OpenLineOps.Operations.Domain.Identifiers;
using OpenLineOps.Operations.Domain.Shared.Enums;

namespace OpenLineOps.Operations.Domain.Repositories;

public interface IAlarmRepository :
    IAggregateRepository<Alarm, AlarmId>
{
    Task<IReadOnlyCollection<Alarm>> GetOpenByStationAsync(
        string stationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<Alarm>> GetByStatusAsync(
        AlarmStatus status,
        CancellationToken cancellationToken = default);
}
