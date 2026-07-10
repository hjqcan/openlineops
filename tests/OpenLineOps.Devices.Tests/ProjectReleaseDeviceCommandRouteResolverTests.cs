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

        var releaseStore = new FileSystemProjectReleaseArtifactStore();
        var release = await releaseStore.PublishAsync(
            scope,
            snapshotId,
            CreatedAtUtc.AddMinutes(1),
            new ProjectReleaseSourceMetadata(
                "topology.release.route",
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
                    "Device",
                    "scanner-provider")],
                [new ProjectReleaseTargetReference("EquipmentNode", "station-eol")],
                ["openlineops_device_command@1"]));

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
                "Device",
                "scanner-provider")],
            [new ProjectTargetReference("EquipmentNode", "station-eol")],
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
        var route = await resolver.ResolveAsync(new DeviceCommandRouteRequest(
            "session-release",
            "step-release",
            "command-release",
            "node-scan",
            "station-eol",
            configurationSnapshotId,
            new OpenLineOps.Devices.Domain.Identifiers.DeviceCapabilityId(capabilityId),
            "Scan",
            projectId,
            applicationId,
            snapshotId));

        Assert.NotNull(route);
        Assert.Equal("scanner-01", route.DeviceInstanceId.Value);
        Assert.Equal(capabilityId, route.CapabilityId.Value);
        Assert.Equal("device.scanner:Scan", route.CommandDefinitionId.Value);
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
        var station = StationProfile.Create(
            new EngineeringStationProfileId("station-eol"),
            "EOL Station");

        Assert.True(recipe.Publish(CreatedAtUtc.AddSeconds(1)).Succeeded);
        Assert.True(station.AddDeviceBinding(DeviceBinding.Create(
            new EngineeringDeviceBindingId("device-binding.scanner"),
            new EngineeringDeviceCapabilityId(capabilityId),
            "scanner-01")).Succeeded);
        Assert.True(project.PublishSnapshot(
            new EngineeringConfigurationSnapshotId(snapshotId),
            new EngineeringProcessDefinitionId(processDefinitionId),
            new EngineeringProcessVersionId(processVersionId),
            recipe,
            station,
            CreatedAtUtc.AddSeconds(2)).Succeeded);

        return project;
    }
}
