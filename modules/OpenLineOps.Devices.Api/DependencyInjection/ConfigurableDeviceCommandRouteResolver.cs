using Microsoft.Extensions.Configuration;
using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Infrastructure.Execution;

namespace OpenLineOps.Devices.Api.DependencyInjection;

internal sealed class ConfigurableDeviceCommandRouteResolver : IDeviceCommandRouteResolver
{
    private readonly IConfiguration _configuration;
    private readonly DeviceRuntimeBridgeOptions _options;
    private readonly ProjectReleaseDeviceCommandRouteResolver _projectReleaseResolver;
    private readonly EngineeringConfigurationDeviceCommandRouteResolver _engineeringResolver;
    private readonly StaticDeviceCommandRouteResolver _staticResolver;

    public ConfigurableDeviceCommandRouteResolver(
        IConfiguration configuration,
        DeviceRuntimeBridgeOptions options,
        ProjectReleaseDeviceCommandRouteResolver projectReleaseResolver,
        EngineeringConfigurationDeviceCommandRouteResolver engineeringResolver,
        StaticDeviceCommandRouteResolver staticResolver)
    {
        _configuration = configuration;
        _options = options;
        _projectReleaseResolver = projectReleaseResolver;
        _engineeringResolver = engineeringResolver;
        _staticResolver = staticResolver;
    }

    public ValueTask<DeviceCommandRoute?> ResolveAsync(
        DeviceCommandRouteRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.HasProjectReleaseIdentity)
        {
            // Published project sessions are release-bound. Never fall back to
            // mutable/global Engineering routes when the immutable release is
            // absent, invalid, or does not contain the requested binding.
            return _projectReleaseResolver.ResolveAsync(request, cancellationToken);
        }

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
