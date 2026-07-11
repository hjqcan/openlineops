using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Runs;

namespace OpenLineOps.Runtime.Infrastructure.Transport;

public sealed class DisabledStationJobGateway : IStationJobGateway
{
    public ValueTask<StationJobCompleted> DispatchAsync(
        StationJobRequested request,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "Station Agent transport is explicitly disabled for this local/test host.");
}

public sealed class DisabledStationSafetyGateway :
    IStationEmergencyStopGateway,
    IStationSafetyGateway,
    IStationJobCancellationGateway
{
    public ValueTask<EmergencyStopAcknowledged> RequestEmergencyStopAsync(
        EmergencyStopRequested request,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "Independent Emergency Stop transport is explicitly disabled for this local/test host.");

    public ValueTask<StationSafeStopAcknowledged> RequestSafeStopAsync(
        StationSafeStopRequested request,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "Independent Station safety transport is explicitly disabled for this local/test host.");

    public ValueTask<StationJobCancelAcknowledged> RequestCancelAsync(
        StationJobCancelRequested request,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "Independent Station control transport is explicitly disabled for this local/test host.");
}
