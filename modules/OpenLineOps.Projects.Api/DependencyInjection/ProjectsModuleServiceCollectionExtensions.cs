using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Projects.Api.Integrations;
using OpenLineOps.Projects.Api.Time;
using OpenLineOps.Projects.Application.ExternalPrograms;
using OpenLineOps.Projects.Application.Persistence;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.ProjectWorkspaces;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Projects.Infrastructure.Persistence;
using OpenLineOps.Projects.Infrastructure.ExternalPrograms;
using OpenLineOps.Projects.Infrastructure.ProjectWorkspaces;
using OpenLineOps.Projects.Infrastructure.Releases;

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
        services.TryAddSingleton<FileSystemProjectReleaseArtifactStore>();
        services.TryAddSingleton<IProjectReleaseArtifactStore>(serviceProvider =>
            serviceProvider.GetRequiredService<FileSystemProjectReleaseArtifactStore>());
        services.TryAddSingleton<IInstalledProjectReleaseReader>(serviceProvider =>
            serviceProvider.GetRequiredService<FileSystemProjectReleaseArtifactStore>());
        services.TryAddSingleton<IExternalProgramResourceRepository, FileSystemExternalProgramResourceRepository>();
        services.TryAddSingleton<IProjectReleaseStationPackagePublisher>(serviceProvider =>
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var section = configuration.GetSection(StationPackagePublicationOptions.SectionName);
            return new FileSystemProjectReleaseStationPackagePublisher(
                new StationPackagePublicationOptions(
                    section["DistributionDirectory"] ?? string.Empty,
                    section["DeploymentCatalogDirectory"] ?? string.Empty,
                    section["SigningKeyId"] ?? string.Empty,
                    section["SigningPrivateKeyPath"] ?? string.Empty));
        });
        services.TryAddSingleton<IProjectReleasePluginCommandResolver, ProjectReleasePluginCommandResolver>();
        services.TryAddScoped<IProjectApplicationWorkspaceScopeResolver, AutomationProjectWorkspaceScopeResolver>();
        services.TryAddScoped<IExternalProgramResourceUsageInspector, ExternalProgramResourceUsageInspector>();
        services.AddScoped<IProjectReleaseSourceResolver, ProjectReleaseSourceResolver>();
        services.AddScoped<IProjectReleasePublisher, ProjectReleasePublisher>();
        services.AddScoped<IProjectReleaseSnapshotReader, ProjectReleaseSnapshotReader>();
        services.AddScoped<IProjectReleaseProductionRunContextService, ProjectReleaseProductionRunContextService>();
        services.AddScoped<IProjectReleaseProductionRunLauncher, ProjectReleaseProductionRunLauncher>();
        services.AddScoped<IExternalProgramResourceService, ExternalProgramResourceService>();
        services.AddScoped<IAutomationProjectService, AutomationProjectService>();
        services.AddScoped<IAutomationProjectWorkspaceService, AutomationProjectWorkspaceService>();

        return services;
    }
}
