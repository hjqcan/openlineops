using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenLineOps.Projects.Application.ProjectWorkspaces;

namespace OpenLineOps.Projects.Api.HostedServices;

public sealed class ProjectsStartupWorkspaceHostedService(
    IServiceScopeFactory scopeFactory,
    ProjectsStartupWorkspaceOptions options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var openedProjectIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var projectFile in options.ProjectFiles)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider
                .GetRequiredService<IAutomationProjectWorkspaceService>();
            var result = await service.OpenAsync(
                    new OpenAutomationProjectWorkspaceRequest(projectFile),
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.IsFailure)
            {
                throw new InvalidOperationException(
                    $"Startup Automation Project '{projectFile}' could not be opened: "
                    + $"{result.Error.Code}: {result.Error.Message}");
            }

            if (!openedProjectIds.Add(result.Value.Project.ProjectId))
            {
                throw new InvalidOperationException(
                    $"Startup Automation Project id '{result.Value.Project.ProjectId}' is "
                    + "declared by more than one configured Project file.");
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
