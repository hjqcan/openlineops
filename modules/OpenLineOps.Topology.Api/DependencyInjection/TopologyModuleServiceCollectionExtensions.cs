using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Topology.Api.Time;
using OpenLineOps.Topology.Application.Persistence;
using OpenLineOps.Topology.Application.Topologies;
using OpenLineOps.Topology.Infrastructure.Persistence;

namespace OpenLineOps.Topology.Api.DependencyInjection;

public static class TopologyModuleServiceCollectionExtensions
{
    public static IServiceCollection AddOpenLineOpsTopologyModule(this IServiceCollection services)
    {
        services.TryAddSingleton<IClock, SystemClock>();

        services.AddSingleton<InMemoryAutomationTopologyRepository>();
        services.AddSingleton<IAutomationTopologyRepository>(serviceProvider =>
            serviceProvider.GetRequiredService<InMemoryAutomationTopologyRepository>());
        services.AddSingleton<InMemorySiteLayoutRepository>();
        services.AddSingleton<ISiteLayoutRepository>(serviceProvider =>
            serviceProvider.GetRequiredService<InMemorySiteLayoutRepository>());
        services.AddScoped<IAutomationTopologyService, AutomationTopologyService>();

        return services;
    }
}
