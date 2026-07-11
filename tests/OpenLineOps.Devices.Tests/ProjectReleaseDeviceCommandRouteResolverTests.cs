using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
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
using OpenLineOps.Projects.Application.ExternalPrograms;
using OpenLineOps.Projects.Domain.Applications;
using OpenLineOps.Projects.Domain.Projects;
using OpenLineOps.Projects.Domain.Snapshots;
using OpenLineOps.Projects.Infrastructure.Persistence;
using OpenLineOps.Projects.Infrastructure.Releases;
using OpenLineOps.Projects.Infrastructure.ExternalPrograms;
using OpenLineOps.Runtime.Contracts;
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

    [Fact]
    public void FrozenBindingSelectionUsesExactSystemDriverAndDeviceResourceOwnership()
    {
        const string capabilityId = "motion.shared";
        var ownerA = new ProjectReleaseCapabilityBinding(
            capabilityId,
            "binding.axis-a",
            "DeviceInstance",
            "axis-a",
            "system.axis-a",
            "station.eol");
        var ownerB = new ProjectReleaseCapabilityBinding(
            capabilityId,
            "binding.axis-b",
            "DeviceInstance",
            "axis-b",
            "system.axis-b",
            "station.eol");
        var otherStation = new ProjectReleaseCapabilityBinding(
            capabilityId,
            "binding.axis-other",
            "DeviceInstance",
            "axis-other",
            "system.axis-other",
            "station.other");
        var operation = SelectionOperation(
            "station.eol",
            [new ProjectReleaseOperationResource(
                "resource.axis-b",
                "Device",
                "binding.axis-b",
                "Fixed",
                [])]);
        var bindings = new[] { ownerA, ownerB, otherStation };

        Assert.Same(ownerB, ProjectReleaseDeviceCommandRouteResolver.SelectTopologyBinding(
            SelectionRequest("System", "system.axis-b"),
            operation,
            bindings));
        Assert.Same(ownerB, ProjectReleaseDeviceCommandRouteResolver.SelectTopologyBinding(
            SelectionRequest("Driver", "binding.axis-b"),
            operation,
            bindings));
        Assert.Same(ownerB, ProjectReleaseDeviceCommandRouteResolver.SelectTopologyBinding(
            SelectionRequest("Capability", capabilityId),
            operation,
            bindings));
        Assert.Null(ProjectReleaseDeviceCommandRouteResolver.SelectTopologyBinding(
            SelectionRequest("Capability", capabilityId),
            SelectionOperation("station.eol", []),
            bindings));
        Assert.Null(ProjectReleaseDeviceCommandRouteResolver.SelectTopologyBinding(
            SelectionRequest("System", "system.axis-b", "station.other"),
            SelectionOperation("station.other", []),
            bindings));
    }

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
                            ["openlineops_device_command@1"],
                            [new ProjectReleaseOperationResource(
                                "resource.station",
                                "Station",
                                "station.eol",
                                "Fixed",
                                [])],
                            [new ProjectReleaseAuthorizedAction(
                                "action-scan",
                                "node-scan",
                                "DeviceCommand",
                                capabilityId,
                                "Scan",
                                "System",
                                "station.eol",
                                30_000,
                                null)])
                    ],
                    Transitions: [],
                    LineControllerAuthorizations: []),
                ExternalProgramResources: [],
                [new ProjectReleaseCapabilityBinding(
                    capabilityId,
                    "binding.scanner",
                    "PluginCommand",
                    "plugin.scanner",
                    "station.eol",
                    "station.eol")],
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
                "plugin.scanner",
                "station.eol",
                "station.eol")],
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
            "operation.eol@0001",
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
            "action-scan",
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
            timeout: TimeSpan.FromSeconds(30),
            resourceLeaseFences: ResourceFences("station.eol"));
        var route = await resolver.ResolveAsync(request);

        var deviceRoute = Assert.IsType<ProjectReleaseDeviceCommandRoute>(route);
        Assert.Equal("scanner-01", deviceRoute.DeviceInstanceId.Value);
        Assert.Equal(capabilityId, deviceRoute.CapabilityId.Value);
        Assert.Equal("device.scanner:scan.updated", deviceRoute.CommandDefinitionId.Value);
        Assert.NotNull(deviceRoute.PluginPackage);
        Assert.Equal("plugin.scanner", deviceRoute.PluginPackage.PluginId);
        Assert.Equal("2.0.0", deviceRoute.PluginPackage.Version);
        Assert.Equal(packageDependency.PackageContentSha256, deviceRoute.PluginPackage.PackageContentSha256);
        Assert.Null(await resolver.ResolveAsync(CopyRequest(request, actionId: "action-forged")));
        Assert.Null(await resolver.ResolveAsync(CopyRequest(request, runtimeNodeId: "node-forged")));
        Assert.Null(await resolver.ResolveAsync(CopyRequest(
            request,
            timeout: TimeSpan.FromSeconds(29))));
        Assert.Null(await resolver.ResolveAsync(new DeviceCommandRouteRequest(
            request.RuntimeSessionId,
            request.ProductionRunId,
            request.ProductionLineDefinitionId,
            request.OperationId,
            request.OperationRunId,
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
            request.ActionId,
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
            request.Timeout,
            request.ResourceLeaseFences)));

        await File.WriteAllTextAsync(
            Path.Combine(
                release.ReleaseRootPath,
                packageDependency.PackageRelativePath.Replace('/', Path.DirectorySeparatorChar),
                "scanner.dll"),
            "tampered-plugin-binary");

        Assert.Null(await resolver.ResolveAsync(request));
    }

    [Fact]
    public async Task ExactLineControllerGrantRoutesThroughLocalControllerAndRequiresRemoteFences()
    {
        const string projectId = "project.line-controller";
        const string applicationId = "application.line-controller";
        const string snapshotId = "snapshot.line-controller";
        const string configurationSnapshotId = "configuration.line-controller";
        const string processDefinitionId = "process.line-controller";
        const string processVersionId = "process.line-controller@1.0.0";
        const string controllerCapabilityId = "device.scanner";
        const string applicationProjectPath =
            "applications/application.line-controller/application.line-controller.oloapp";
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
            controllerCapabilityId));
        await engineeringRepository.SaveAsync(scope, CreateStationProfile(controllerCapabilityId));
        var packagePath = Path.Combine(_projectPath, "line-controller-plugin");
        Directory.CreateDirectory(packagePath);
        await File.WriteAllTextAsync(
            Path.Combine(packagePath, "manifest.json"),
            "{\"id\":\"plugin.scanner\"}");
        await File.WriteAllTextAsync(
            Path.Combine(packagePath, "scanner.dll"),
            "line-controller-plugin-binary");
        var packageDependency = CreatePackageDependency(packagePath, controllerCapabilityId);
        WriteReleaseTopologyResources(
            scope,
            topologyId: "topology.line-controller",
            layoutId: "layout.line-controller",
            lineDefinitionId: "line.line-controller");

        var releaseBindings = new[]
        {
            new ProjectReleaseCapabilityBinding(
                controllerCapabilityId,
                "binding.scanner",
                "PluginCommand",
                "plugin.scanner",
                "station.eol",
                "station.eol"),
            new ProjectReleaseCapabilityBinding(
                controllerCapabilityId,
                "binding.scanner-secondary",
                "DeviceInstance",
                "scanner-secondary",
                "system.scanner-secondary",
                "station.eol"),
            new ProjectReleaseCapabilityBinding(
                "remote.inspect",
                "binding.remote-inspector",
                "DeviceInstance",
                "remote-inspector",
                "system.remote-inspector",
                "station.remote")
        };
        var releaseTargets = new[]
        {
            new ProjectReleaseTargetReference("System", "station.eol"),
            new ProjectReleaseTargetReference("System", "system.scanner-secondary"),
            new ProjectReleaseTargetReference("System", "station.remote"),
            new ProjectReleaseTargetReference("System", "system.remote-inspector"),
            new ProjectReleaseTargetReference("Driver", "binding.remote-inspector")
        };
        var authorization = new ProjectReleaseLineControllerAuthorization(
            "authorization.remote-inspect",
            "operation.controller",
            "action-remote-inspect",
            "station.eol",
            "binding.scanner",
            controllerCapabilityId,
            "Scan",
            "station.remote",
            "system.remote-inspector",
            "binding.remote-inspector",
            "remote.inspect",
            "Inspect");
        var releaseStore = new FileSystemProjectReleaseArtifactStore();
        var release = await releaseStore.PublishAsync(
            scope,
            snapshotId,
            CreatedAtUtc.AddMinutes(1),
            new ProjectReleaseSourceMetadata(
                "topology.line-controller",
                ["layout.line-controller"],
                new ProjectReleaseProductionLine(
                    "line.line-controller",
                    "Line Controller",
                    "topology.line-controller",
                    new ProjectReleaseProductModel("product.main", "MAIN", "serialNumber"),
                    "operation.controller",
                    [new ProjectReleaseOperation(
                        "operation.controller",
                        "Controller",
                        "station.eol",
                        processDefinitionId,
                        configurationSnapshotId,
                        processVersionId,
                        "openlineops.flow-ir",
                        "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a",
                        "{}",
                        ["openlineops_device_command@1"],
                        [
                            new ProjectReleaseOperationResource(
                                "resource.station",
                                "Station",
                                "station.eol",
                                "Fixed",
                                []),
                            new ProjectReleaseOperationResource(
                                "resource.controller",
                                "Device",
                                "binding.scanner",
                                "Fixed",
                                [])
                        ],
                        [new ProjectReleaseAuthorizedAction(
                            "action-remote-inspect",
                            "node-controller",
                            "DeviceCommand",
                            controllerCapabilityId,
                            "Scan",
                            "Driver",
                            "binding.remote-inspector",
                            30_000,
                            authorization.AuthorizationId)])],
                    [],
                    [authorization]),
                [],
                releaseBindings,
                releaseTargets,
                ["openlineops_device_command@1"],
                [packageDependency]));

        var project = AutomationProject.Create(
            new ProjectId(projectId),
            "Line Controller Project",
            _projectPath,
            CreatedAtUtc);
        var projectApplicationId = new ProjectApplicationId(applicationId);
        var topologyId = new ProjectTopologyId("topology.line-controller");
        Assert.True(project.AddApplication(ProjectApplication.Create(
            projectApplicationId,
            "Line Controller Application",
            applicationProjectPath)).Succeeded);
        Assert.True(project.LinkTopology(projectApplicationId, topologyId).Succeeded);
        Assert.True(project.LinkProcessDefinition(
            projectApplicationId,
            new ProjectDefinitionId(processDefinitionId)).Succeeded);
        Assert.True(project.PublishSnapshot(
            new ProjectSnapshotId(snapshotId),
            projectApplicationId,
            topologyId,
            ["layout.line-controller"],
            new ProjectProductionLineDefinitionId("line.line-controller"),
            releaseBindings.Select(binding => new SnapshotCapabilityBinding(
                binding.CapabilityId,
                binding.BindingId,
                binding.ProviderKind,
                binding.ProviderKey,
                binding.OwnerSystemId,
                binding.OwnerStationSystemId)).ToArray(),
            releaseTargets.Select(target => new ProjectTargetReference(
                target.Kind,
                target.TargetId)).ToArray(),
            ["openlineops_device_command@1"],
            Path.GetRelativePath(_projectPath, release.ManifestPath).Replace('\\', '/'),
            release.ContentSha256,
            CreatedAtUtc.AddMinutes(1)).Succeeded);
        var projectRepository = new InMemoryAutomationProjectRepository();
        await projectRepository.SaveAsync(project);
        var resolver = new ProjectReleaseDeviceCommandRouteResolver(
            projectRepository,
            releaseStore,
            engineeringRepository);
        var request = new DeviceCommandRouteRequest(
            "session-controller",
            "00000000-0000-0000-0000-000000000011",
            "line.line-controller",
            "operation.controller",
            "operation.controller@0001",
            1,
            "product.main",
            "serialNumber",
            "SERIAL-001",
            null,
            null,
            null,
            null,
            "step-controller",
            "command-controller",
            "node-controller",
            "action-remote-inspect",
            "station.eol",
            configurationSnapshotId,
            new OpenLineOps.Devices.Domain.Identifiers.DeviceCapabilityId(controllerCapabilityId),
            "Scan",
            projectId,
            applicationId,
            snapshotId,
            "Driver",
            "binding.remote-inspector",
            null,
            TimeSpan.FromSeconds(30),
            LineControllerFences());

        var route = Assert.IsType<ProjectReleaseLineControllerCommandRoute>(
            await resolver.ResolveAsync(request));
        Assert.Equal("binding.remote-inspector", route.TargetBindingId);
        Assert.Equal("remote.inspect", route.TargetCapabilityId);
        Assert.Equal("Inspect", route.TargetAction);
        Assert.Equal("scanner-01", route.ControllerRoute.DeviceInstanceId.Value);
        Assert.Null(await resolver.ResolveAsync(CopyRequest(request, actionId: "action-forged")));
        Assert.Null(await resolver.ResolveAsync(new DeviceCommandRouteRequest(
            request.RuntimeSessionId,
            request.ProductionRunId,
            request.ProductionLineDefinitionId,
            request.OperationId,
            request.OperationRunId,
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
            request.ActionId,
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
            request.Timeout,
            LineControllerFences(includeRemoteDevice: false))));

        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(release.ManifestPath))!;
        manifest["metadata"]!["productionLine"]!["lineControllerAuthorizations"]![0]!["targetAction"] = "Forged";
        await File.WriteAllTextAsync(release.ManifestPath, manifest.ToJsonString());
        Assert.Null(await resolver.ResolveAsync(request));
    }

    [Fact]
    public async Task ExternalProgramRouteUsesExactOperationAndFrozenResource()
    {
        const string projectId = "project.external.route";
        const string applicationId = "application.external.route";
        const string snapshotId = "snapshot.external.route";
        const string configurationSnapshotId = "configuration.external.route";
        const string processDefinitionId = "process.external.route";
        const string processVersionId = "process.external.route@1.0.0";
        const string capabilityId = "test.external";
        const string resourceId = "resource.external";
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

        var executableBytes = Encoding.UTF8.GetBytes("frozen-external-program");
        await using var executableContent = new MemoryStream(executableBytes, writable: false);
        var externalResource = await new FileSystemExternalProgramResourceRepository().SaveAsync(
            scope,
            new SaveExternalProgramResourceRequest(
                resourceId,
                "External EOL",
                capabilityId,
                "ExecuteExternalProgram",
                ExternalProgramLaunchKind.ApplicationExecutable,
                "files/test.exe",
                ProviderKind: null,
                ProviderKey: null,
                ["--serial", "{{product.identity}}", "--operation", "{{operation.id}}"],
                [
                    new ExternalProgramInputMapping("$product.identity", "serial"),
                    new ExternalProgramInputMapping("$product.model", "model"),
                    new ExternalProgramInputMapping("$run.id", "runId"),
                    new ExternalProgramInputMapping("$operation.id", "operationId")
                ],
                [
                    new ExternalProgramResultMapping(
                        "$.outcome",
                        "test.outcome",
                        ProductionContextValueKind.Text)
                ],
                new ExternalProgramOutcomeMapping(
                    "$.outcome",
                    "Passed",
                    "Failed",
                    "Aborted"),
                new ExternalProgramPermissionProfile(
                    "Restricted",
                    NetworkAccessAllowed: false,
                    AllowedEnvironmentVariables: []),
                new ExternalProgramExecutionLimits(
                    TimeoutMilliseconds: 30_000,
                    MaximumProcessCount: 4,
                    MaximumWorkingSetBytes: 512L * 1024 * 1024,
                    MaximumCpuTimeMilliseconds: 30_000,
                    MaximumStandardOutputBytes: 4 * 1024 * 1024,
                    MaximumStandardErrorBytes: 4 * 1024 * 1024,
                    MaximumArtifactCount: 64,
                    MaximumArtifactBytes: 64L * 1024 * 1024,
                    MaximumTotalArtifactBytes: 256L * 1024 * 1024)),
            [
                new ExternalProgramFileUpload(
                    "files/test.exe",
                    executableContent,
                    executableBytes.LongLength,
                    Convert.ToHexString(SHA256.HashData(executableBytes)).ToLowerInvariant())
            ],
            CreatedAtUtc);

        const string resourceRelativePath = "external-programs/resource.external";
        var frozenResource = new ProjectReleaseExternalProgramResource(
            externalResource.ResourceId,
            externalResource.DisplayName,
            externalResource.CapabilityId,
            externalResource.CommandName,
            externalResource.LaunchKind.ToString(),
            externalResource.EntryPoint,
            externalResource.ProviderKind,
            externalResource.ProviderKey,
            externalResource.ArgumentTemplates,
            externalResource.InputMappings.Select(mapping =>
                new ProjectReleaseExternalProgramInputMapping(mapping.Source, mapping.Target)).ToArray(),
            externalResource.ResultMappings.Select(mapping =>
                new ProjectReleaseExternalProgramResultMapping(
                    mapping.SourcePath,
                    mapping.TargetKey,
                    mapping.ValueKind.ToString())).ToArray(),
            new ProjectReleaseExternalProgramOutcomeMapping(
                externalResource.OutcomeMapping.SourcePath,
                externalResource.OutcomeMapping.PassedToken,
                externalResource.OutcomeMapping.FailedToken,
                externalResource.OutcomeMapping.AbortedToken),
            new ProjectReleaseExternalProgramPermissionProfile(
                externalResource.PermissionProfile.ProfileName,
                externalResource.PermissionProfile.NetworkAccessAllowed,
                externalResource.PermissionProfile.AllowedEnvironmentVariables),
            new ProjectReleaseExternalProgramExecutionLimits(
                externalResource.ExecutionLimits.TimeoutMilliseconds,
                externalResource.ExecutionLimits.MaximumProcessCount,
                externalResource.ExecutionLimits.MaximumWorkingSetBytes,
                externalResource.ExecutionLimits.MaximumCpuTimeMilliseconds,
                externalResource.ExecutionLimits.MaximumStandardOutputBytes,
                externalResource.ExecutionLimits.MaximumStandardErrorBytes,
                externalResource.ExecutionLimits.MaximumArtifactCount,
                externalResource.ExecutionLimits.MaximumArtifactBytes,
                externalResource.ExecutionLimits.MaximumTotalArtifactBytes),
            externalResource.Files.Select(file => new ProjectReleaseExternalProgramFile(
                $"{resourceRelativePath}/{file.RelativePath}",
                file.SizeBytes,
                file.Sha256)).ToArray(),
            externalResource.ContentSha256,
            resourceRelativePath);

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
                    "External Program Line",
                    "topology.external.route",
                    new ProjectReleaseProductModel("product.external", "MODEL-EXTERNAL", "serialNumber"),
                    "operation.external",
                    [
                        new ProjectReleaseOperation(
                            "operation.external",
                            "External Program",
                            "station.eol",
                            processDefinitionId,
                            configurationSnapshotId,
                            processVersionId,
                            "openlineops.flow-ir",
                            "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a",
                            "{}",
                            ["openlineops_device_command@1"],
                            [new ProjectReleaseOperationResource(
                                "resource.station",
                                "Station",
                                "station.eol",
                                "Fixed",
                                [])],
                            [new ProjectReleaseAuthorizedAction(
                                "action-external",
                                "node-external",
                                "DeviceCommand",
                                capabilityId,
                                "ExecuteExternalProgram",
                                "System",
                                "station.eol",
                                30_000,
                                null)])
                    ],
                    Transitions: [],
                    LineControllerAuthorizations: []),
                [frozenResource],
                [new ProjectReleaseCapabilityBinding(
                    capabilityId,
                    "binding.external",
                    "ExternalSystem",
                    resourceId,
                    "station.eol",
                    "station.eol")],
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
                resourceId,
                "station.eol",
                "station.eol")],
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
            "operation.external@0001",
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
            "action-external",
            "station.eol",
            configurationSnapshotId,
            new OpenLineOps.Devices.Domain.Identifiers.DeviceCapabilityId(capabilityId),
            "ExecuteExternalProgram",
            projectId,
            applicationId,
            snapshotId,
            "System",
            "station.eol",
            $$"""{"externalProgramResourceId":"{{resourceId}}"}""",
            TimeSpan.FromSeconds(30),
            ResourceFences("station.eol"));

        var route = Assert.IsType<ProjectReleaseExternalProgramCommandRoute>(
            await resolver.ResolveAsync(request));
        Assert.Equal(resourceId, route.ResourceId);
        Assert.Equal("files/test.exe", route.EntryPoint);
        Assert.Equal(resourceRelativePath, route.ResourceRelativePath);
        Assert.Null(route.ProviderRoute);
        Assert.Equal("ApplicationExecutable", route.LaunchKind);
        Assert.Contains(route.Files, file => file.RelativePath == "resource.json");
        Assert.Contains(route.Files, file => file.RelativePath == "files/test.exe");
        Assert.StartsWith(
            Path.GetFullPath(release.SourceRootPath),
            Path.GetFullPath(route.ReleaseApplicationRootPath),
            StringComparison.OrdinalIgnoreCase);
        var frozenExecutable = Path.Combine(
            route.ReleaseApplicationRootPath,
            route.ResourceRelativePath.Replace('/', Path.DirectorySeparatorChar),
            route.EntryPoint!.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(frozenExecutable));

        Assert.Null(await resolver.ResolveAsync(CopyRequest(
            request,
            operationId: "operation.other")));
        Assert.Null(await resolver.ResolveAsync(CopyRequest(
            request,
            productionLineDefinitionId: "line.other")));
        Assert.Null(await resolver.ResolveAsync(CopyRequest(
            request,
            productModelId: "product.other")));

        await File.WriteAllTextAsync(frozenExecutable, "tampered-external-program");
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
        string? productModelId = null,
        string? actionId = null,
        string? runtimeNodeId = null,
        TimeSpan? timeout = null)
    {
        return new DeviceCommandRouteRequest(
            request.RuntimeSessionId,
            request.ProductionRunId,
            productionLineDefinitionId ?? request.ProductionLineDefinitionId,
            operationId ?? request.OperationId,
            operationId is null
                ? request.OperationRunId
                : $"{operationId}@{request.OperationAttempt:D4}",
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
            runtimeNodeId ?? request.RuntimeNodeId,
            actionId ?? request.ActionId,
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
            timeout ?? request.Timeout,
            request.ResourceLeaseFences);
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
            "station.eol",
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
            "station.eol",
            "station.eol",
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

    private static ProjectReleaseOperation SelectionOperation(
        string stationSystemId,
        IReadOnlyCollection<ProjectReleaseOperationResource> resources) => new(
        "operation.select",
        "Select binding",
        stationSystemId,
        "flow.select",
        "configuration.select",
        "flow.select@1",
        "openlineops.flow-ir",
        new string('a', 64),
        "{}",
        [],
        resources,
        []);

    private static DeviceCommandRouteRequest SelectionRequest(
        string targetKind,
        string targetId,
        string stationSystemId = "station.eol") => new(
        "session-select",
        "00000000-0000-0000-0000-000000000099",
        "line.select",
        "operation.select",
        "operation.select@0001",
        1,
        "product.select",
        "serialNumber",
        "SERIAL-SELECT",
        null,
        null,
        null,
        null,
        "step-select",
        "command-select",
        "node-select",
        "action-select",
        stationSystemId,
        "configuration.select",
        new OpenLineOps.Devices.Domain.Identifiers.DeviceCapabilityId("motion.shared"),
        "Move",
        "project.select",
        "application.select",
        "snapshot.select",
        targetKind,
        targetId,
        null,
        TimeSpan.FromSeconds(30),
        ResourceFences(stationSystemId));

    private static DeviceCommandResourceFenceEvidence[] ResourceFences(string stationSystemId) =>
        [new DeviceCommandResourceFenceEvidence(
            "Station",
            stationSystemId,
            1,
            new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero))];

    private static DeviceCommandResourceFenceEvidence[] LineControllerFences(
        bool includeRemoteDevice = true)
    {
        var fences = new List<DeviceCommandResourceFenceEvidence>
        {
            new("Station", "station.eol", 1, new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            new("Device", "binding.scanner", 2, new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            new("Station", "station.remote", 3, new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero))
        };
        if (includeRemoteDevice)
        {
            fences.Add(new DeviceCommandResourceFenceEvidence(
                "Device",
                "binding.remote-inspector",
                4,
                new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        }

        return fences.ToArray();
    }
}
