using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Infrastructure.Execution;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Projects;
using OpenLineOps.Engineering.Domain.Recipes;
using OpenLineOps.Engineering.Domain.Stations;
using OpenLineOps.Engineering.Infrastructure.Persistence;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Projects.Domain.Applications;
using OpenLineOps.Projects.Domain.Projects;
using OpenLineOps.Projects.Domain.Snapshots;
using OpenLineOps.Projects.Infrastructure.Persistence;
using OpenLineOps.Projects.Infrastructure.Releases;
using ProjectApplicationId = OpenLineOps.Projects.Domain.Identifiers.ProjectApplicationId;
using ProjectConfigurationSnapshotId = OpenLineOps.Projects.Domain.Identifiers.ConfigurationSnapshotId;
using ProjectDefinitionId = OpenLineOps.Projects.Domain.Identifiers.ProcessDefinitionId;
using ProjectId = OpenLineOps.Projects.Domain.Identifiers.AutomationProjectId;
using ProjectProcessVersionId = OpenLineOps.Projects.Domain.Identifiers.ProcessVersionId;
using ProjectSnapshotId = OpenLineOps.Projects.Domain.Identifiers.PublishedProjectSnapshotId;
using ProjectTopologyId = OpenLineOps.Projects.Domain.Identifiers.AutomationTopologyId;
using EngineeringConfigurationSnapshotId = OpenLineOps.Engineering.Domain.Identifiers.ConfigurationSnapshotId;
using EngineeringDeviceBindingId = OpenLineOps.Engineering.Domain.Identifiers.DeviceBindingId;
using EngineeringDeviceCapabilityId = OpenLineOps.Engineering.Domain.Identifiers.DeviceCapabilityId;
using EngineeringProcessDefinitionId = OpenLineOps.Engineering.Domain.Identifiers.ProcessDefinitionId;
using EngineeringProcessVersionId = OpenLineOps.Engineering.Domain.Identifiers.ProcessVersionId;
using EngineeringRecipeId = OpenLineOps.Engineering.Domain.Identifiers.RecipeId;
using EngineeringRecipeVersionId = OpenLineOps.Engineering.Domain.Identifiers.RecipeVersionId;
using EngineeringStationProfileId = OpenLineOps.Engineering.Domain.Identifiers.StationProfileId;
using EngineeringWorkspaceId = OpenLineOps.Engineering.Domain.Identifiers.WorkspaceId;

namespace OpenLineOps.Devices.Tests;

public sealed class ProjectReleaseDeviceCommandRouteResolverTests : IDisposable
{
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 7, 10, 6, 0, 0, TimeSpan.Zero);

    private readonly string _projectPath = Path.Combine(
        Path.GetTempPath(),
        "openlineops-release-device-route-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PublishedProjectCommandUsesFrozenReleaseAfterEditableSourceIsDeleted()
    {
        const string projectId = "project.release.route";
        const string applicationId = "application.release.route";
        const string snapshotId = "snapshot.release.route";
        const string configurationSnapshotId = "configuration.release.route";
        const string processDefinitionId = "process.release.route";
        const string processVersionId = "process.release.route@1.0.0";
        const string capabilityId = "device.scanner";
        const string applicationProjectPath =
            "applications/application.release.route/application.release.route.oloapp";

        var scope = new ProjectApplicationWorkspaceScope(
            projectId,
            applicationId,
            _projectPath,
            applicationProjectPath);
        var engineeringRepository = new FileSystemProjectEngineeringConfigurationRepository();
        await engineeringRepository.SaveAsync(scope, CreateEngineeringProject(
            configurationSnapshotId,
            processDefinitionId,
            processVersionId,
            capabilityId));
        await engineeringRepository.SaveAsync(scope, CreateStationProfile(capabilityId));
        var packagePath = Path.Combine(_projectPath, "plugin-source");
        Directory.CreateDirectory(packagePath);
        await File.WriteAllTextAsync(Path.Combine(packagePath, "manifest.json"), "{\"id\":\"plugin.scanner\"}");
        await File.WriteAllTextAsync(Path.Combine(packagePath, "scanner.dll"), "scanner-plugin-binary");
        var packageDependency = CreatePackageDependency(packagePath, capabilityId);
        WriteReleaseTopologyResources(scope);

        var releaseStore = new FileSystemProjectReleaseArtifactStore();
        var release = await releaseStore.PublishAsync(
            scope,
            snapshotId,
            CreatedAtUtc.AddMinutes(1),
            new ProjectReleaseSourceMetadata(
                "topology.release.route",
                "station.eol",
                ["layout.release.route"],
                processDefinitionId,
                processVersionId,
                "openlineops.flow-ir/v1",
                "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a",
                "{}",
                configurationSnapshotId,
                [new ProjectReleaseCapabilityBinding(
                    capabilityId,
                    "binding.scanner",
                    "PluginCommand",
                    "plugin.scanner")],
                [new ProjectReleaseTargetReference("System", "station.eol")],
                ["openlineops_device_command@1"],
                [packageDependency]));

        var project = AutomationProject.Create(
            new ProjectId(projectId),
            "Release Route Project",
            _projectPath,
            CreatedAtUtc);
        var projectApplicationId = new ProjectApplicationId(applicationId);
        var topologyId = new ProjectTopologyId("topology.release.route");
        var projectProcessId = new ProjectDefinitionId(processDefinitionId);
        Assert.True(project.AddApplication(ProjectApplication.Create(
            projectApplicationId,
            "Release Route Application",
            applicationProjectPath)).Succeeded);
        Assert.True(project.LinkTopology(projectApplicationId, topologyId).Succeeded);
        Assert.True(project.LinkProcessDefinition(projectApplicationId, projectProcessId).Succeeded);
        Assert.True(project.PublishSnapshot(
            new ProjectSnapshotId(snapshotId),
            projectApplicationId,
            topologyId,
            ["layout.release.route"],
            projectProcessId,
            new ProjectProcessVersionId(processVersionId),
            new ProjectConfigurationSnapshotId(configurationSnapshotId),
            [new SnapshotCapabilityBinding(
                capabilityId,
                "binding.scanner",
                "PluginCommand",
                "plugin.scanner")],
            [new ProjectTargetReference("System", "station.eol")],
            ["openlineops_device_command@1"],
            Path.GetRelativePath(_projectPath, release.ManifestPath).Replace('\\', '/'),
            release.ContentSha256,
            CreatedAtUtc.AddMinutes(1)).Succeeded);

        var projectRepository = new InMemoryAutomationProjectRepository();
        await projectRepository.SaveAsync(project);

        Directory.Delete(Path.Combine(_projectPath, "applications"), recursive: true);

        var resolver = new ProjectReleaseDeviceCommandRouteResolver(
            projectRepository,
            releaseStore,
            engineeringRepository);
        var request = new DeviceCommandRouteRequest(
            "session-release",
            "step-release",
            "command-release",
            "node-scan",
            "station.eol",
            configurationSnapshotId,
            new OpenLineOps.Devices.Domain.Identifiers.DeviceCapabilityId(capabilityId),
            "Scan",
            projectId,
            applicationId,
            snapshotId,
            "System",
            "station.eol");
        var route = await resolver.ResolveAsync(request);

        var deviceRoute = Assert.IsType<ProjectReleaseDeviceCommandRoute>(route);
        Assert.Equal("scanner-01", deviceRoute.DeviceInstanceId.Value);
        Assert.Equal(capabilityId, deviceRoute.CapabilityId.Value);
        Assert.Equal("device.scanner:scan.v2", deviceRoute.CommandDefinitionId.Value);
        Assert.NotNull(deviceRoute.PluginPackage);
        Assert.Equal("plugin.scanner", deviceRoute.PluginPackage.PluginId);
        Assert.Equal("2.0.0", deviceRoute.PluginPackage.Version);
        Assert.Equal(packageDependency.PackageContentSha256, deviceRoute.PluginPackage.PackageContentSha256);

        await File.WriteAllTextAsync(
            Path.Combine(
                release.ReleaseRootPath,
                packageDependency.PackageRelativePath.Replace('/', Path.DirectorySeparatorChar),
                "scanner.dll"),
            "tampered-plugin-binary");

        Assert.Null(await resolver.ResolveAsync(request));
    }

    private static void WriteReleaseTopologyResources(ProjectApplicationWorkspaceScope scope)
    {
        var topologyDirectory = Path.Combine(scope.ApplicationRootPath, "topology");
        var layoutDirectory = Path.Combine(scope.ApplicationRootPath, "layouts");
        Directory.CreateDirectory(topologyDirectory);
        Directory.CreateDirectory(layoutDirectory);
        File.WriteAllText(
            Path.Combine(topologyDirectory, "topology.json"),
            $$"""
            {"schemaVersion":"openlineops.automation-topology/v1","resourceKind":"OpenLineOps.AutomationTopology","applicationId":"{{scope.ApplicationId}}","topologyId":"topology.release.route"}
            """);
        File.WriteAllText(
            Path.Combine(layoutDirectory, "layout.json"),
            $$"""
            {"schemaVersion":"openlineops.site-layout/v1","resourceKind":"OpenLineOps.SiteLayout","applicationId":"{{scope.ApplicationId}}","layoutId":"layout.release.route"}
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectPath))
        {
            Directory.Delete(_projectPath, recursive: true);
        }
    }

    private static EngineeringProject CreateEngineeringProject(
        string snapshotId,
        string processDefinitionId,
        string processVersionId,
        string capabilityId)
    {
        var project = EngineeringProject.Create(
            new EngineeringProjectId("engineering.release.route"),
            new EngineeringWorkspaceId("workspace.release.route"),
            "Release Engineering",
            CreatedAtUtc);
        var recipe = Recipe.Create(
            new EngineeringRecipeId("recipe.release.route"),
            new EngineeringRecipeVersionId("recipe.release.route@1.0.0"),
            "Release Recipe",
            CreatedAtUtc);
        var station = CreateStationProfile(capabilityId);

        Assert.True(recipe.Publish(CreatedAtUtc.AddSeconds(1)).Succeeded);
        Assert.True(project.PublishSnapshot(
            new EngineeringConfigurationSnapshotId(snapshotId),
            new EngineeringProcessDefinitionId(processDefinitionId),
            new EngineeringProcessVersionId(processVersionId),
            recipe,
            station,
            CreatedAtUtc.AddSeconds(2)).Succeeded);

        return project;
    }

    private static StationProfile CreateStationProfile(string capabilityId)
    {
        var station = StationProfile.Create(
            new EngineeringStationProfileId("station-eol"),
            "station.eol",
            "EOL Station");
        Assert.True(station.AddDeviceBinding(DeviceBinding.Create(
            new EngineeringDeviceBindingId("device-binding.scanner"),
            new EngineeringDeviceCapabilityId(capabilityId),
            "scanner-01")).Succeeded);
        return station;
    }

    private static ProjectReleasePackageDependencyLock CreatePackageDependency(
        string packagePath,
        string capabilityId)
    {
        var files = Directory.EnumerateFiles(packagePath, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var bytes = File.ReadAllBytes(path);
                return new ProjectReleasePackageFile(
                    Path.GetRelativePath(packagePath, path).Replace('\\', '/'),
                    bytes.LongLength,
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            })
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var canonical = new StringBuilder();
        foreach (var file in files)
        {
            canonical.Append(file.RelativePath)
                .Append('\0')
                .Append(file.SizeBytes.ToString(CultureInfo.InvariantCulture))
                .Append('\0')
                .Append(file.Sha256)
                .Append('\n');
        }

        var contentSha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
        return new ProjectReleasePackageDependencyLock(
            capabilityId,
            "binding.scanner",
            "PluginCommand",
            "plugin.scanner",
            "plugin.scanner",
            "plugin.scanner",
            "2.0.0",
            contentSha256,
            files.Single(file => file.RelativePath == "manifest.json").Sha256,
            files.Single(file => file.RelativePath == "scanner.dll").Sha256,
            "1.0.0",
            "win-x64",
            "openlineops.plugin-abi/1",
            $"packages/{contentSha256}",
            "manifest.json",
            "scanner.dll",
            [new ProjectReleasePackageCommandLock(
                "Device",
                "device.scanner:scan.v2",
                capabilityId,
                "Scan")],
            files,
            packagePath);
    }
}
