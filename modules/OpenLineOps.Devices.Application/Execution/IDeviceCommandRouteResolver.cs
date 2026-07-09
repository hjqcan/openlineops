namespace OpenLineOps.Devices.Application.Execution;

public interface IDeviceCommandRouteResolver
{
    ValueTask<DeviceCommandRoute?> ResolveAsync(
        DeviceCommandRouteRequest request,
        CancellationToken cancellationToken = default);
}
