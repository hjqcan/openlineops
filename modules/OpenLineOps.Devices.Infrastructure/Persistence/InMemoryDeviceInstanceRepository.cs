using System.Collections.Concurrent;
using OpenLineOps.Devices.Application.Persistence;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Domain.Instances;

namespace OpenLineOps.Devices.Infrastructure.Persistence;

public sealed class InMemoryDeviceInstanceRepository : IDeviceInstanceRepository
{
    private readonly ConcurrentDictionary<string, DeviceInstance> _instances = new(StringComparer.Ordinal);

    public ValueTask SaveAsync(
        DeviceInstance instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        cancellationToken.ThrowIfCancellationRequested();

        _instances[instance.Id.Value] = instance;

        return ValueTask.CompletedTask;
    }

    public ValueTask<DeviceInstance?> GetByIdAsync(
        DeviceInstanceId instanceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        cancellationToken.ThrowIfCancellationRequested();

        _instances.TryGetValue(instanceId.Value, out var instance);

        return ValueTask.FromResult(instance);
    }

    public ValueTask<IReadOnlyCollection<DeviceInstance>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<IReadOnlyCollection<DeviceInstance>>(
            _instances.Values
                .OrderBy(instance => instance.Id.Value, StringComparer.Ordinal)
                .ToArray());
    }

    public ValueTask<IReadOnlyCollection<DeviceInstance>> ListByStationAsync(
        string stationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stationId))
        {
            return ValueTask.FromResult<IReadOnlyCollection<DeviceInstance>>([]);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<IReadOnlyCollection<DeviceInstance>>(
            _instances.Values
                .Where(instance => string.Equals(instance.StationId, stationId.Trim(), StringComparison.Ordinal))
                .OrderBy(instance => instance.Id.Value, StringComparer.Ordinal)
                .ToArray());
    }
}
