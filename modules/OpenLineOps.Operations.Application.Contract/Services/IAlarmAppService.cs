using OpenLineOps.Operations.Application.Contract.Alarms;
using OpenLineOps.Operations.Application.Contract.Results;

namespace OpenLineOps.Operations.Application.Contract.Services;

public interface IAlarmAppService
{
    Task<AlarmDetails> RaiseAsync(
        RaiseAlarmRequest request,
        CancellationToken cancellationToken = default);

    Task<AlarmDetails?> GetAsync(
        string id,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<AlarmDetails>> GetOpenByStationAsync(
        string stationId,
        CancellationToken cancellationToken = default);

    Task<OperationsApplicationResult> AcknowledgeAsync(
        string id,
        AcknowledgeAlarmRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationsApplicationResult> ResolveAsync(
        string id,
        ResolveAlarmRequest request,
        CancellationToken cancellationToken = default);
}
