namespace OpenLineOps.Devices.Application.Execution;

public interface IProjectReleaseRuntimeCommandRouteResolver
{
    ValueTask<ProjectReleaseRuntimeCommandRoute?> ResolveAsync(
        DeviceCommandRouteRequest request,
        CancellationToken cancellationToken = default);
}
