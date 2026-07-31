using OpenLineOps.Domain.Abstractions.Repositories;
using OpenLineOps.Operations.Domain.Aggregates;
using OpenLineOps.Operations.Domain.Identifiers;
using OpenLineOps.Operations.Domain.Shared.Enums;

namespace OpenLineOps.Operations.Domain.Repositories;

public interface IAlarmRepository :
    IAggregateRepository<Alarm, AlarmId>
{
    Task<AlarmCommitOutcome> CommitAlarmChangesAsync(
        CancellationToken cancellationToken = default);

    Task<AlarmDefinition?> GetDefinitionAsync(
        string id,
        CancellationToken cancellationToken = default);

    Task<AlarmDefinition?> GetDefinitionByCommandIdAsync(
        string commandId,
        CancellationToken cancellationToken = default);

    void AddDefinition(AlarmDefinition definition);

    Task<AlarmLifecycleFact?> GetFactByCommandIdAsync(
        string commandId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<AlarmLifecycleFact>> GetFactsAsync(
        AlarmId alarmId,
        CancellationToken cancellationToken = default);

    Task<string> GetLastFactHashAsync(
        AlarmId alarmId,
        CancellationToken cancellationToken = default);

    void AddFact(AlarmLifecycleFact fact);

    void UpdateWithExpectedVersion(Alarm aggregate, long expectedVersion);

    Task<IReadOnlyCollection<Alarm>> GetOpenByStationAsync(
        string stationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<Alarm>> GetByStatusAsync(
        AlarmStatus status,
        CancellationToken cancellationToken = default);
}
