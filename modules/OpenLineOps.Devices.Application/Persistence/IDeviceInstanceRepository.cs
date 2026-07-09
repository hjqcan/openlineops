using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Domain.Instances;

namespace OpenLineOps.Devices.Application.Persistence;

public interface IDeviceInstanceRepository
{
    ValueTask SaveAsync(
        DeviceInstance instance,
        CancellationToken cancellationToken = default);

    ValueTask<DeviceInstance?> GetByIdAsync(
        DeviceInstanceId instanceId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<DeviceInstance>> ListAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<DeviceInstance>> ListByStationAsync(
        string stationId,
        CancellationToken cancellationToken = default);
}
