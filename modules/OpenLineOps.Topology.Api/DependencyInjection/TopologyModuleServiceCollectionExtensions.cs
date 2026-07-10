using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Topology.Api.Time;
using OpenLineOps.Topology.Application.Persistence;
using OpenLineOps.Topology.Application.ProjectWorkspaces;
using OpenLineOps.Topology.Infrastructure.Persistence;

namespace OpenLineOps.Topology.Api.DependencyInjection;

public static class TopologyModuleServiceCollectionExtensions
{
    public static IServiceCollection AddOpenLineOpsTopologyModule(this IServiceCollection services)
    {
        services.TryAddSingleton<IClock, SystemClock>();

        services.TryAddSingleton<IProjectAutomationTopologyRepository, FileSystemProjectAutomationTopologyRepository>();
        services.TryAddSingleton<IProjectSiteLayoutRepository, FileSystemProjectSiteLayoutRepository>();
        services.AddScoped<IProjectAutomationTopologyService, ProjectAutomationTopologyService>();

        return services;
    }
}
