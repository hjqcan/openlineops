using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Domain.Identifiers;

namespace OpenLineOps.Devices.Infrastructure.Execution;

public sealed class StaticDeviceCommandRouteResolver : IDeviceCommandRouteResolver
{
    public ValueTask<DeviceCommandRoute?> ResolveAsync(
        DeviceCommandRouteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedCommandName = request.CommandName.Replace(" ", "-", StringComparison.Ordinal);

        DeviceCommandRoute? route = new DeviceCommandRoute(
            new DeviceInstanceId($"{request.StationId}:{request.CapabilityId.Value}"),
            new DeviceCommandDefinitionId($"{request.CapabilityId.Value}:{normalizedCommandName}"),
            request.CapabilityId);

        return ValueTask.FromResult<DeviceCommandRoute?>(route);
    }
}
