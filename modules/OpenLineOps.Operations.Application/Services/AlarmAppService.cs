using OpenLineOps.Operations.Application.Contract.Alarms;
using OpenLineOps.Operations.Application.Contract.Results;
using OpenLineOps.Operations.Application.Contract.Services;
using OpenLineOps.Operations.Domain.Aggregates;
using OpenLineOps.Operations.Domain.Identifiers;
using OpenLineOps.Operations.Domain.Repositories;

namespace OpenLineOps.Operations.Application.Services;

public sealed class AlarmAppService(IAlarmRepository repository)
    : IAlarmAppService
{
    public async Task<AlarmDetails> RaiseAsync(
        RaiseAlarmRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var aggregate = Alarm.Raise(
            new AlarmId(request.Id),
            request.StationId,
            request.Source,
            request.SourceId,
            request.Severity,
            request.Title,
            request.Description,
            request.RaisedAtUtc ?? DateTimeOffset.UtcNow);

        repository.Add(aggregate);

        var committed = await repository.UnitOfWork.Commit().ConfigureAwait(false);
        if (!committed)
        {
            throw new InvalidOperationException("Alarm raise operation did not persist any changes.");
        }

        return ToDetails(aggregate);
    }

    public async Task<AlarmDetails?> GetAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var aggregate = await repository
            .GetByIdAsync(new AlarmId(id), cancellationToken)
            .ConfigureAwait(false);

        return aggregate is null
            ? null
            : ToDetails(aggregate);
    }

    public async Task<IReadOnlyCollection<AlarmDetails>> GetOpenByStationAsync(
        string stationId,
        CancellationToken cancellationToken = default)
    {
        var alarms = await repository
            .GetOpenByStationAsync(stationId, cancellationToken)
            .ConfigureAwait(false);

        return alarms.Select(ToDetails).ToArray();
    }

    public async Task<OperationsApplicationResult> AcknowledgeAsync(
        string id,
        AcknowledgeAlarmRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var aggregate = await repository
            .GetByIdAsync(new AlarmId(id), cancellationToken)
            .ConfigureAwait(false);
        if (aggregate is null)
        {
            return OperationsApplicationResult.Rejected(
                "Operations.Alarm.NotFound",
                "Alarm was not found.");
        }

        var result = aggregate.Acknowledge(request.AcknowledgedBy, DateTimeOffset.UtcNow);
        if (!result.Succeeded)
        {
            return OperationsApplicationResult.Rejected(result.Code, result.Message);
        }

        repository.Update(aggregate);

        var committed = await repository.UnitOfWork.Commit().ConfigureAwait(false);
        return committed
            ? OperationsApplicationResult.Accepted(result.Message)
            : OperationsApplicationResult.Rejected(
                "Operations.Alarm.NotPersisted",
                "Alarm acknowledgement did not persist any changes.");
    }

    public async Task<OperationsApplicationResult> ResolveAsync(
        string id,
        ResolveAlarmRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var aggregate = await repository
            .GetByIdAsync(new AlarmId(id), cancellationToken)
            .ConfigureAwait(false);
        if (aggregate is null)
        {
            return OperationsApplicationResult.Rejected(
                "Operations.Alarm.NotFound",
                "Alarm was not found.");
        }

        var result = aggregate.Resolve(request.ResolvedBy, request.ResolutionNote, DateTimeOffset.UtcNow);
        if (!result.Succeeded)
        {
            return OperationsApplicationResult.Rejected(result.Code, result.Message);
        }

        repository.Update(aggregate);

        var committed = await repository.UnitOfWork.Commit().ConfigureAwait(false);
        return committed
            ? OperationsApplicationResult.Accepted(result.Message)
            : OperationsApplicationResult.Rejected(
                "Operations.Alarm.NotPersisted",
                "Alarm resolution did not persist any changes.");
    }

    private static AlarmDetails ToDetails(Alarm aggregate)
    {
        return new AlarmDetails(
            aggregate.Id.Value,
            aggregate.StationId,
            aggregate.Source,
            aggregate.SourceId,
            aggregate.Severity,
            aggregate.Status,
            aggregate.Title,
            aggregate.Description,
            aggregate.RaisedAtUtc,
            aggregate.AcknowledgedBy,
            aggregate.AcknowledgedAtUtc,
            aggregate.ResolvedBy,
            aggregate.ResolvedAtUtc,
            aggregate.ResolutionNote);
    }
}
