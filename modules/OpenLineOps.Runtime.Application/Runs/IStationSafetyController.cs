using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Runs;

public interface IStationSafetyController
{
    ValueTask<StationSafetyResult> RequestSafeStopAsync(
        StationSafetyRequest request,
        CancellationToken cancellationToken = default);
}

public interface IStationSafetyGateway
{
    ValueTask<StationSafeStopAcknowledged> RequestSafeStopAsync(
        StationSafeStopRequested request,
        CancellationToken cancellationToken = default);
}

public sealed record StationSafetyRequest(
    ProductionRunSnapshot Run,
    string ActorId,
    string Reason);

public sealed record StationSafetyResult(bool Accepted, string? FailureCode, string? FailureReason)
{
    public static StationSafetyResult Success() => new(true, null, null);

    public static StationSafetyResult Failure(string code, string reason) =>
        new(false, code, reason);
}
