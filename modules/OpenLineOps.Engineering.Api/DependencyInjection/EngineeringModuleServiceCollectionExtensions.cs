using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Engineering.Application.ProjectWorkspaces;
using OpenLineOps.Engineering.Infrastructure.Persistence;
using OpenLineOps.Engineering.Infrastructure.Processes;
using OpenLineOps.Engineering.Infrastructure.Time;
using OpenLineOps.Processes.Application.Runtime;

namespace OpenLineOps.Engineering.Api.DependencyInjection;

public static class EngineeringModuleServiceCollectionExtensions
{
    public static IServiceCollection AddOpenLineOpsEngineeringModule(this IServiceCollection services)
    {
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<
            IProjectEngineeringConfigurationRepository,
            FileSystemProjectEngineeringConfigurationRepository>();
        services.AddScoped<IProjectEngineeringConfigurationService, ProjectEngineeringConfigurationService>();
        services.AddScoped<
            IProjectRuntimeConfigurationSnapshotResolver,
            ProjectEngineeringRuntimeConfigurationSnapshotResolver>();

        return services;
    }
}
