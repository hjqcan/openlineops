using Microsoft.Extensions.Configuration;
using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Infrastructure.Execution;

namespace OpenLineOps.Devices.Api.DependencyInjection;

internal sealed class ConfigurableDeviceCommandRouteResolver : IDeviceCommandRouteResolver
{
    private readonly IConfiguration _configuration;
    private readonly DeviceRuntimeBridgeOptions _options;
    private readonly EngineeringConfigurationDeviceCommandRouteResolver _engineeringResolver;
    private readonly StaticDeviceCommandRouteResolver _staticResolver;

    public ConfigurableDeviceCommandRouteResolver(
        IConfiguration configuration,
        DeviceRuntimeBridgeOptions options,
        EngineeringConfigurationDeviceCommandRouteResolver engineeringResolver,
        StaticDeviceCommandRouteResolver staticResolver)
    {
        _configuration = configuration;
        _options = options;
        _engineeringResolver = engineeringResolver;
        _staticResolver = staticResolver;
    }

    public ValueTask<DeviceCommandRoute?> ResolveAsync(
        DeviceCommandRouteRequest request,
        CancellationToken cancellationToken = default)
    {
        var provider = _configuration[$"{DeviceRuntimeBridgeOptions.DevicesRoutingSectionName}:Provider"]
            ?? _options.RouteResolver;

        if (DeviceCommandRouteResolvers.IsStatic(provider))
        {
            return _staticResolver.ResolveAsync(request, cancellationToken);
        }

        if (DeviceCommandRouteResolvers.IsEngineering(provider))
        {
            return _engineeringResolver.ResolveAsync(request, cancellationToken);
        }

        throw new InvalidOperationException(
            $"Unsupported device command route resolver '{provider}'.");
    }
}
