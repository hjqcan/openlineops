using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Projects.Api.Time;
using OpenLineOps.Projects.Api.Integrations;
using OpenLineOps.Projects.Application.Persistence;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.ProjectWorkspaces;
using OpenLineOps.Projects.Infrastructure.Persistence;
using OpenLineOps.Projects.Infrastructure.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Projects.Api.DependencyInjection;

public static class ProjectsModuleServiceCollectionExtensions
{
    public static IServiceCollection AddOpenLineOpsProjectsModule(this IServiceCollection services)
    {
        services.TryAddSingleton<IClock, SystemClock>();
        services.AddSingleton<InMemoryAutomationProjectRepository>();
        services.AddSingleton<IAutomationProjectRepository>(serviceProvider =>
            serviceProvider.GetRequiredService<InMemoryAutomationProjectRepository>());
        services.TryAddSingleton<IAutomationProjectManifestStore, FileSystemAutomationProjectManifestStore>();
        services.TryAddScoped<IProjectApplicationWorkspaceScopeResolver, AutomationProjectWorkspaceScopeResolver>();
        services.AddScoped<IAutomationProjectService, AutomationProjectService>();
        services.AddScoped<IAutomationProjectWorkspaceService, AutomationProjectWorkspaceService>();

        return services;
    }
}
