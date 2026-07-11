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
using EngineeringConfigurationSnapshotId = OpenLineOps.Engineering.Domain.Identifiers.ConfigurationSnapshotId;
using EngineeringDeviceBindingId = OpenLineOps.Engineering.Domain.Identifiers.DeviceBindingId;
using EngineeringDeviceCapabilityId = OpenLineOps.Engineering.Domain.Identifiers.DeviceCapabilityId;
using EngineeringProcessDefinitionId = OpenLineOps.Engineering.Domain.Identifiers.ProcessDefinitionId;
using EngineeringProcessVersionId = OpenLineOps.Engineering.Domain.Identifiers.ProcessVersionId;
using EngineeringRecipeId = OpenLineOps.Engineering.Domain.Identifiers.RecipeId;
using EngineeringRecipeVersionId = OpenLineOps.Engineering.Domain.Identifiers.RecipeVersionId;
using EngineeringStationProfileId = OpenLineOps.Engineering.Domain.Identifiers.StationProfileId;
using EngineeringWorkspaceId = OpenLineOps.Engineering.Domain.Identifiers.WorkspaceId;
using ProjectApplicationId = OpenLineOps.Projects.Domain.Identifiers.ProjectApplicationId;
using ProjectDefinitionId = OpenLineOps.Projects.Domain.Identifiers.ProcessDefinitionId;
using ProjectId = OpenLineOps.Projects.Domain.Identifiers.AutomationProjectId;
using ProjectProductionLineDefinitionId = OpenLineOps.Projects.Domain.Identifiers.ProductionLineDefinitionId;
using ProjectSnapshotId = OpenLineOps.Projects.Domain.Identifiers.PublishedProjectSnapshotId;
using ProjectTopologyId = OpenLineOps.Projects.Domain.Identifiers.AutomationTopologyId;

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
                ["layout.release.route"],
                new ProjectReleaseProductionLine(
                    "line.release.route",
                    "Release Route Line",
                    "topology.release.route",
                    new ProjectReleaseProductModel("product.release", "MAINBOARD-A", "serialNumber"),
                    "operation.eol",
                    [
                        new ProjectReleaseOperation(
                            "operation.eol",
                            "EOL",
                            "station.eol",
                            processDefinitionId,
                            configurationSnapshotId,
                            processVersionId,
                            "openlineops.flow-ir",
                            "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a",
                            "{}",
                            ["openlineops_device_command@1"])
                    ],
                    Transitions: [],
                    ExternalTestProgramAdapters: []),
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
            new ProjectProductionLineDefinitionId("line.release.route"),
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
            "00000000-0000-0000-0000-000000000010",
            "line.release.route",
            "operation.eol",
            1,
            "product.release",
            "serialNumber",
            "SERIAL-001",
            null,
            null,
            null,
            null,
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
            "station.eol",
            inputPayload: null,
            timeout: TimeSpan.FromSeconds(30));
        var route = await resolver.ResolveAsync(request);

        var deviceRoute = Assert.IsType<ProjectReleaseDeviceCommandRoute>(route);
        Assert.Equal("scanner-01", deviceRoute.DeviceInstanceId.Value);
        Assert.Equal(capabilityId, deviceRoute.CapabilityId.Value);
        Assert.Equal("device.scanner:scan.updated", deviceRoute.CommandDefinitionId.Value);
        Assert.NotNull(deviceRoute.PluginPackage);
        Assert.Equal("plugin.scanner", deviceRoute.PluginPackage.PluginId);
        Assert.Equal("2.0.0", deviceRoute.PluginPackage.Version);
        Assert.Equal(packageDependency.PackageContentSha256, deviceRoute.PluginPackage.PackageContentSha256);
        Assert.Null(await resolver.ResolveAsync(new DeviceCommandRouteRequest(
            request.RuntimeSessionId,
            request.ProductionRunId,
            request.ProductionLineDefinitionId,
            request.OperationId,
            request.OperationAttempt,
            request.ProductModelId,
            request.ProductionUnitIdentityInputKey,
            request.ProductionUnitIdentityValue,
            request.LotId,
            request.CarrierId,
            request.FixtureId,
            request.DeviceId,
            request.RuntimeStepId,
            request.RuntimeCommandId,
            request.RuntimeNodeId,
            request.StationSystemId,
            request.ConfigurationSnapshotId,
            request.CapabilityId,
            "scan",
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            request.TargetKind,
            request.TargetId,
            request.InputPayload,
            request.Timeout)));

        await File.WriteAllTextAsync(
            Path.Combine(
                release.ReleaseRootPath,
                packageDependency.PackageRelativePath.Replace('/', Path.DirectorySeparatorChar),
                "scanner.dll"),
            "tampered-plugin-binary");

        Assert.Null(await resolver.ResolveAsync(request));
    }

    [Fact]
    public async Task ExternalTestProgramRouteUsesExactOperationAndFrozenExecutable()
    {
        const string projectId = "project.external.route";
        const string applicationId = "application.external.route";
        const string snapshotId = "snapshot.external.route";
        const string configurationSnapshotId = "configuration.external.route";
        const string processDefinitionId = "process.external.route";
        const string processVersionId = "process.external.route@1.0.0";
        const string capabilityId = "test.external";
        const string adapterId = "adapter.external";
        const string applicationProjectPath =
            "applications/application.external.route/application.external.route.oloapp";

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
        WriteReleaseTopologyResources(
            scope,
            topologyId: "topology.external.route",
            layoutId: "layout.external.route",
            lineDefinitionId: "line.external.route");
        var executableSourcePath = Path.Combine(
            scope.ApplicationRootPath,
            "programs",
            "external",
            "test.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executableSourcePath)!);
        await File.WriteAllTextAsync(executableSourcePath, "frozen-external-test-program");

        var releaseStore = new FileSystemProjectReleaseArtifactStore();
        var release = await releaseStore.PublishAsync(
            scope,
            snapshotId,
            CreatedAtUtc.AddMinutes(1),
            new ProjectReleaseSourceMetadata(
                "topology.external.route",
                ["layout.external.route"],
                new ProjectReleaseProductionLine(
                    "line.external.route",
                    "External Test Line",
                    "topology.external.route",
                    new ProjectReleaseProductModel("product.external", "MODEL-EXTERNAL", "serialNumber"),
                    "operation.external",
                    [
                        new ProjectReleaseOperation(
                            "operation.external",
                            "External Test",
                            "station.eol",
                            processDefinitionId,
                            configurationSnapshotId,
                            processVersionId,
                            "openlineops.flow-ir",
                            "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a",
                            "{}",
                            ["openlineops_device_command@1"])
                    ],
                    Transitions: [],
                    ExternalTestProgramAdapters:
                    [
                        new ProjectReleaseExternalTestProgramAdapter(
                            adapterId,
                            "External EOL",
                            capabilityId,
                            "ExecuteTestProgram",
                            "ApplicationExecutable",
                            "programs/external/test.exe",
                            ProviderKey: null,
                            ["--serial", "{{product.identity}}", "--operation", "{{operation.id}}"],
                            [
                                new ProjectReleaseExternalTestProgramInputMapping(
                                    "$product.identity",
                                    "serial"),
                                new ProjectReleaseExternalTestProgramInputMapping(
                                    "$product.model",
                                    "model"),
                                new ProjectReleaseExternalTestProgramInputMapping(
                                    "$run.id",
                                    "runId"),
                                new ProjectReleaseExternalTestProgramInputMapping(
                                    "$operation.id",
                                    "operationId")
                            ],
                            [
                                new ProjectReleaseExternalTestProgramResultMapping(
                                    "$.outcome",
                                    "test.outcome")
                            ],
                            new ProjectReleaseExternalTestProgramOutcomeMapping(
                                "$.outcome",
                                "Passed",
                                "Failed",
                                "Aborted"),
                            30_000)
                    ]),
                [new ProjectReleaseCapabilityBinding(
                    capabilityId,
                    "binding.external",
                    "ExternalSystem",
                    adapterId)],
                [new ProjectReleaseTargetReference("System", "station.eol")],
                ["openlineops_device_command@1"],
                PackageDependencies: []));

        var project = AutomationProject.Create(
            new ProjectId(projectId),
            "External Route Project",
            _projectPath,
            CreatedAtUtc);
        var projectApplicationId = new ProjectApplicationId(applicationId);
        var topologyId = new ProjectTopologyId("topology.external.route");
        Assert.True(project.AddApplication(ProjectApplication.Create(
            projectApplicationId,
            "External Route Application",
            applicationProjectPath)).Succeeded);
        Assert.True(project.LinkTopology(projectApplicationId, topologyId).Succeeded);
        Assert.True(project.LinkProcessDefinition(
            projectApplicationId,
            new ProjectDefinitionId(processDefinitionId)).Succeeded);
        Assert.True(project.PublishSnapshot(
            new ProjectSnapshotId(snapshotId),
            projectApplicationId,
            topologyId,
            ["layout.external.route"],
            new ProjectProductionLineDefinitionId("line.external.route"),
            [new SnapshotCapabilityBinding(
                capabilityId,
                "binding.external",
                "ExternalSystem",
                adapterId)],
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
            "session-external",
            "00000000-0000-0000-0000-000000000020",
            "line.external.route",
            "operation.external",
            1,
            "product.external",
            "serialNumber",
            "SERIAL-EXT-001",
            null,
            null,
            null,
            null,
            "step-external",
            "command-external",
            "node-external",
            "station.eol",
            configurationSnapshotId,
            new OpenLineOps.Devices.Domain.Identifiers.DeviceCapabilityId(capabilityId),
            "ExecuteTestProgram",
            projectId,
            applicationId,
            snapshotId,
            "System",
            "station.eol",
            $$"""{"externalTestProgramAdapterId":"{{adapterId}}"}""",
            TimeSpan.FromSeconds(30));

        var route = Assert.IsType<ProjectReleaseExternalTestProgramCommandRoute>(
            await resolver.ResolveAsync(request));
        Assert.Equal(adapterId, route.AdapterId);
        var frozenExecutable = Assert.IsType<string>(route.Executable);
        Assert.Equal("programs/external/test.exe", frozenExecutable);
        Assert.Null(route.ProviderRoute);
        Assert.Equal("ApplicationExecutable", route.LaunchKind);
        Assert.StartsWith(
            Path.GetFullPath(release.SourceRootPath),
            Path.GetFullPath(route.ReleaseApplicationRootPath),
            StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(
            route.ReleaseApplicationRootPath,
            frozenExecutable.Replace('/', Path.DirectorySeparatorChar))));

        Assert.Null(await resolver.ResolveAsync(CopyRequest(
            request,
            operationId: "operation.other")));
        Assert.Null(await resolver.ResolveAsync(CopyRequest(
            request,
            productionLineDefinitionId: "line.other")));
        Assert.Null(await resolver.ResolveAsync(CopyRequest(
            request,
            productModelId: "product.other")));

        await File.WriteAllTextAsync(
            Path.Combine(
                route.ReleaseApplicationRootPath,
                frozenExecutable.Replace('/', Path.DirectorySeparatorChar)),
            "tampered-external-test-program");
        Assert.Null(await resolver.ResolveAsync(request));
    }

    private static void WriteReleaseTopologyResources(
        ProjectApplicationWorkspaceScope scope,
        string topologyId = "topology.release.route",
        string layoutId = "layout.release.route",
        string lineDefinitionId = "line.release.route")
    {
        var topologyDirectory = Path.Combine(scope.ApplicationRootPath, "topology");
        var layoutDirectory = Path.Combine(scope.ApplicationRootPath, "layouts");
        var productionDirectory = Path.Combine(
            scope.ApplicationRootPath,
            "production",
            "lines",
            lineDefinitionId);
        Directory.CreateDirectory(topologyDirectory);
        Directory.CreateDirectory(layoutDirectory);
        Directory.CreateDirectory(productionDirectory);
        File.WriteAllText(
            Path.Combine(topologyDirectory, "topology.json"),
            $$"""
            {"schemaVersion":"openlineops.automation-topology","resourceKind":"OpenLineOps.AutomationTopology","applicationId":"{{scope.ApplicationId}}","topologyId":"{{topologyId}}"}
            """);
        File.WriteAllText(
            Path.Combine(layoutDirectory, "layout.json"),
            $$"""
            {"schemaVersion":"openlineops.site-layout","resourceKind":"OpenLineOps.SiteLayout","applicationId":"{{scope.ApplicationId}}","layoutId":"{{layoutId}}"}
            """);
        File.WriteAllText(
            Path.Combine(productionDirectory, "line.json"),
            $$"""
            {"schemaVersion":"openlineops.production-line","resourceKind":"OpenLineOps.ProductionLine","applicationId":"{{scope.ApplicationId}}","lineDefinitionId":"{{lineDefinitionId}}"}
            """);
    }

    private static DeviceCommandRouteRequest CopyRequest(
        DeviceCommandRouteRequest request,
        string? productionLineDefinitionId = null,
        string? operationId = null,
        string? productModelId = null)
    {
        return new DeviceCommandRouteRequest(
            request.RuntimeSessionId,
            request.ProductionRunId,
            productionLineDefinitionId ?? request.ProductionLineDefinitionId,
            operationId ?? request.OperationId,
            request.OperationAttempt,
            productModelId ?? request.ProductModelId,
            request.ProductionUnitIdentityInputKey,
            request.ProductionUnitIdentityValue,
            request.LotId,
            request.CarrierId,
            request.FixtureId,
            request.DeviceId,
            request.RuntimeStepId,
            request.RuntimeCommandId,
            request.RuntimeNodeId,
            request.StationSystemId,
            request.ConfigurationSnapshotId,
            request.CapabilityId,
            request.CommandName,
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            request.TargetKind,
            request.TargetId,
            request.InputPayload,
            request.Timeout);
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
                "device.scanner:scan.updated",
                capabilityId,
                "Scan")],
            files,
            packagePath);
    }
}
