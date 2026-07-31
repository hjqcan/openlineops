using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Projects.Api.DependencyInjection;
using OpenLineOps.Projects.Api.HostedServices;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.ProjectWorkspaces;
using OpenLineOps.Runtime.Api.DependencyInjection;
using OpenLineOps.Runtime.Api.HostedServices;

namespace OpenLineOps.Api.Tests;

public sealed class ProjectsStartupWorkspaceHostedServiceTests
{
    [Fact]
    public void ApiCompositionRegistersWorkspaceRestoreBeforeRuntimeRecovery()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:Coordination:Provider"] = "InMemory",
                ["OpenLineOps:Runtime:AgentTransport:Provider"] = "Disabled",
                ["OpenLineOps:Runtime:StationExecution:Provider"] = "InProcess",
                ["OpenLineOps:Runtime:Persistence:Provider"] = "InMemory"
            }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOpenLineOpsProjectsModule();
        services.AddOpenLineOpsRuntimeModule(configuration);

        var hostedTypes = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(static descriptor => descriptor.ImplementationType)
            .ToArray();
        var workspaceIndex = Array.IndexOf(
            hostedTypes,
            typeof(ProjectsStartupWorkspaceHostedService));
        var recoveryIndex = Array.IndexOf(
            hostedTypes,
            typeof(ProductionRunStartupRecoveryHostedService));

        Assert.True(workspaceIndex >= 0);
        Assert.True(recoveryIndex >= 0);
        Assert.True(workspaceIndex < recoveryIndex);
    }

    [Fact]
    public void ConfigurationRequiresCanonicalContiguousUniqueProjectFiles()
    {
        var first = ProjectFile("first");
        var second = ProjectFile("second");
        var options = LoadOptions(new Dictionary<string, string?>
        {
            [$"{ProjectsStartupWorkspaceOptions.SectionName}:ProjectFiles:0"] = first,
            [$"{ProjectsStartupWorkspaceOptions.SectionName}:ProjectFiles:1"] = second
        });

        Assert.Equal([first, second], options.ProjectFiles);

        Assert.Throws<InvalidOperationException>(() => LoadOptions(new Dictionary<string, string?>
        {
            [$"{ProjectsStartupWorkspaceOptions.SectionName}:ProjectFiles:0"] = first,
            [$"{ProjectsStartupWorkspaceOptions.SectionName}:ProjectFiles:2"] = second
        }));
        Assert.Throws<InvalidOperationException>(() => LoadOptions(new Dictionary<string, string?>
        {
            [$"{ProjectsStartupWorkspaceOptions.SectionName}:ProjectFiles:0"] = first,
            [$"{ProjectsStartupWorkspaceOptions.SectionName}:ProjectFiles:1"] = first
        }));
        Assert.Throws<InvalidOperationException>(() => LoadOptions(new Dictionary<string, string?>
        {
            [$"{ProjectsStartupWorkspaceOptions.SectionName}:ProjectFiles:0"] = "relative.oloproj"
        }));
        Assert.Throws<InvalidOperationException>(() => LoadOptions(new Dictionary<string, string?>
        {
            [$"{ProjectsStartupWorkspaceOptions.SectionName}:ProjectFiles:0"] = first,
            [$"{ProjectsStartupWorkspaceOptions.SectionName}:IgnoreFailures"] = "true"
        }));
    }

    [Fact]
    public async Task OpensEveryConfiguredWorkspaceBeforeReturningFromStartup()
    {
        var first = ProjectFile("first");
        var second = ProjectFile("second");
        var workspaceService = new RecordingWorkspaceService(new Dictionary<string, string>
        {
            [first] = "project.first",
            [second] = "project.second"
        });
        await using var provider = Services(workspaceService);
        var hostedService = new ProjectsStartupWorkspaceHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            LoadOptions(new Dictionary<string, string?>
            {
                [$"{ProjectsStartupWorkspaceOptions.SectionName}:ProjectFiles:0"] = first,
                [$"{ProjectsStartupWorkspaceOptions.SectionName}:ProjectFiles:1"] = second
            }));

        await hostedService.StartAsync(CancellationToken.None);

        Assert.Equal([first, second], workspaceService.OpenedProjectFiles);
    }

    [Fact]
    public async Task FailsHostStartupForInvalidWorkspaceOrDuplicateProjectIdentity()
    {
        var missing = ProjectFile("missing");
        var failingService = new RecordingWorkspaceService(
            new Dictionary<string, string>(StringComparer.Ordinal));
        await using (var provider = Services(failingService))
        {
            var hostedService = new ProjectsStartupWorkspaceHostedService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                LoadOptions(new Dictionary<string, string?>
                {
                    [$"{ProjectsStartupWorkspaceOptions.SectionName}:ProjectFiles:0"] = missing
                }));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                hostedService.StartAsync(CancellationToken.None));
            Assert.Contains("Projects.ManifestNotFound", exception.Message, StringComparison.Ordinal);
        }

        var first = ProjectFile("duplicate-first");
        var second = ProjectFile("duplicate-second");
        var duplicateService = new RecordingWorkspaceService(new Dictionary<string, string>
        {
            [first] = "project.duplicate",
            [second] = "project.duplicate"
        });
        await using (var provider = Services(duplicateService))
        {
            var hostedService = new ProjectsStartupWorkspaceHostedService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                LoadOptions(new Dictionary<string, string?>
                {
                    [$"{ProjectsStartupWorkspaceOptions.SectionName}:ProjectFiles:0"] = first,
                    [$"{ProjectsStartupWorkspaceOptions.SectionName}:ProjectFiles:1"] = second
                }));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                hostedService.StartAsync(CancellationToken.None));
            Assert.Contains("declared by more than one", exception.Message, StringComparison.Ordinal);
        }
    }

    private static ProjectsStartupWorkspaceOptions LoadOptions(
        IReadOnlyDictionary<string, string?> values) =>
        ProjectsStartupWorkspaceOptions.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    private static ServiceProvider Services(IAutomationProjectWorkspaceService workspaceService)
    {
        var services = new ServiceCollection();
        services.AddSingleton(workspaceService);
        return services.BuildServiceProvider();
    }

    private static string ProjectFile(string suffix) =>
        Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            $"openlineops-startup-{suffix}.oloproj"));

    private sealed class RecordingWorkspaceService(
        IReadOnlyDictionary<string, string> projects) : IAutomationProjectWorkspaceService
    {
        public List<string> OpenedProjectFiles { get; } = [];

        public Task<Result<AutomationProjectWorkspaceDetails>> OpenAsync(
            OpenAutomationProjectWorkspaceRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenedProjectFiles.Add(request.ProjectPath);
            if (!projects.TryGetValue(request.ProjectPath, out var projectId))
            {
                return Task.FromResult(Result.Failure<AutomationProjectWorkspaceDetails>(
                    ApplicationError.NotFound(
                        "Projects.ManifestNotFound",
                        $"Project manifest {request.ProjectPath} was not found.")));
            }

            var now = DateTimeOffset.UnixEpoch;
            return Task.FromResult(Result.Success(new AutomationProjectWorkspaceDetails(
                new AutomationProjectDetails(
                    projectId,
                    projectId,
                    Path.GetDirectoryName(request.ProjectPath)!,
                    now,
                    null,
                    [],
                    []),
                request.ProjectPath,
                new AutomationProjectManifest(
                    AutomationProjectManifest.CurrentFormatVersion,
                    AutomationProjectManifest.ProductName,
                    projectId,
                    projectId,
                    Path.GetDirectoryName(request.ProjectPath)!,
                    now,
                    now,
                    null,
                    [],
                    []))));
        }

        public Task<Result<AutomationProjectWorkspaceDetails>> CreateAsync(
            CreateAutomationProjectWorkspaceRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<AutomationProjectWorkspaceDetails>> SaveManifestAsync(
            string projectId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<AutomationProjectWorkspaceDetails>> ImportApplicationAsync(
            string projectId,
            ImportAutomationProjectApplicationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
