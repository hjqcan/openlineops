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

public interface IStationEmergencyStopGateway
{
    ValueTask<EmergencyStopAcknowledged> RequestEmergencyStopAsync(
        EmergencyStopRequested request,
        CancellationToken cancellationToken = default);
}

public sealed record StationSafetyRequest(
    ProductionRunSnapshot Run,
    string ActorId,
    string Reason,
    DateTimeOffset RequestedAtUtc);

public sealed record StationSafetyResult(
    bool Accepted,
    string? FailureCode,
    string? FailureReason,
    DateTimeOffset? AcknowledgedAtUtc)
{
    public static StationSafetyResult Success(DateTimeOffset? acknowledgedAtUtc = null) =>
        new(true, null, null, acknowledgedAtUtc);

    public static StationSafetyResult Failure(string code, string reason) =>
        new(false, code, reason, null);
}
