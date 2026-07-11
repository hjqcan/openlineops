using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Snapshots;
using OpenLineOps.Projects.Application.Persistence;
using OpenLineOps.Projects.Application.ExternalPrograms;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Projects.Domain.Identifiers;
using DeviceCapabilityId = OpenLineOps.Devices.Domain.Identifiers.DeviceCapabilityId;
using DeviceCommandDefinitionId = OpenLineOps.Devices.Domain.Identifiers.DeviceCommandDefinitionId;
using DeviceInstanceId = OpenLineOps.Devices.Domain.Identifiers.DeviceInstanceId;

namespace OpenLineOps.Devices.Infrastructure.Execution;

public sealed class ProjectReleaseDeviceCommandRouteResolver : IProjectReleaseRuntimeCommandRouteResolver
{
    private readonly IAutomationProjectRepository _projectRepository;
    private readonly IProjectReleaseArtifactStore _releaseStore;
    private readonly IProjectEngineeringConfigurationRepository _configurationRepository;

    public ProjectReleaseDeviceCommandRouteResolver(
        IAutomationProjectRepository projectRepository,
        IProjectReleaseArtifactStore releaseStore,
        IProjectEngineeringConfigurationRepository configurationRepository)
    {
        _projectRepository = projectRepository;
        _releaseStore = releaseStore;
        _configurationRepository = configurationRepository;
    }

    public async ValueTask<ProjectReleaseRuntimeCommandRoute?> ResolveAsync(
        DeviceCommandRouteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var project = await _projectRepository
            .GetByIdAsync(new AutomationProjectId(request.ProjectId), cancellationToken)
            .ConfigureAwait(false);
        if (project is null)
        {
            return null;
        }

        var snapshot = project.Snapshots.SingleOrDefault(candidate =>
            string.Equals(candidate.Id.Value, request.ProjectSnapshotId, StringComparison.Ordinal)
            && string.Equals(candidate.ApplicationId.Value, request.ApplicationId, StringComparison.Ordinal));
        var application = project.Applications.SingleOrDefault(candidate =>
            string.Equals(candidate.Id.Value, request.ApplicationId, StringComparison.Ordinal));
        if (snapshot is null || application is null)
        {
            return null;
        }

        var scope = new ProjectApplicationWorkspaceScope(
            project.Id.Value,
            application.Id.Value,
            project.ProjectPath,
            application.ProjectFilePath);
        OpenedProjectReleaseArtifact? release;
        try
        {
            release = await _releaseStore
                .OpenAsync(scope, snapshot.Id.Value, snapshot.ReleaseContentSha256, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return null;
        }

        if (release is null)
        {
            return null;
        }

        var line = release.Metadata.ProductionLine;
        if (!string.Equals(
                line.LineDefinitionId,
                request.ProductionLineDefinitionId,
                StringComparison.Ordinal)
            || !string.Equals(
                line.ProductModel.ProductModelId,
                request.ProductModelId,
                StringComparison.Ordinal)
            || !string.Equals(
                line.ProductModel.IdentityInputKey,
                request.ProductionUnitIdentityInputKey,
                StringComparison.Ordinal))
        {
            return null;
        }

        var operationMatches = line.Operations
            .Where(operation => string.Equals(
                                    operation.OperationId,
                                    request.OperationId,
                                    StringComparison.Ordinal)
                                && string.Equals(
                                    operation.ConfigurationSnapshotId,
                                    request.ConfigurationSnapshotId,
                                    StringComparison.Ordinal)
                                && string.Equals(
                                    operation.StationSystemId,
                                    request.StationSystemId,
                                    StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (operationMatches.Length != 1)
        {
            return null;
        }

        var operationRoute = operationMatches[0];
        if (!ResourceFencesMatchFrozenOperation(
                line.LineDefinitionId,
                operationRoute,
                line.LineControllerAuthorizations,
                request.ResourceLeaseFences))
        {
            return null;
        }

        long timeoutMilliseconds;
        try
        {
            timeoutMilliseconds = checked(request.Timeout.Ticks / TimeSpan.TicksPerMillisecond);
        }
        catch (OverflowException)
        {
            return null;
        }

        var authorizedActions = operationRoute.AuthorizedActions
            .Where(action => string.Equals(action.ActionId, request.ActionId, StringComparison.Ordinal)
                && string.Equals(action.NodeId, request.RuntimeNodeId, StringComparison.Ordinal)
                && string.Equals(action.Kind, "DeviceCommand", StringComparison.Ordinal)
                && string.Equals(
                    action.RequiredCapability,
                    request.CapabilityId.Value,
                    StringComparison.Ordinal)
                && string.Equals(action.CommandName, request.CommandName, StringComparison.Ordinal)
                && string.Equals(action.TargetKind, request.TargetKind, StringComparison.Ordinal)
                && string.Equals(action.TargetId, request.TargetId, StringComparison.Ordinal)
                && action.TimeoutMilliseconds == timeoutMilliseconds)
            .Take(2)
            .ToArray();
        if (authorizedActions.Length != 1)
        {
            return null;
        }

        var authorizedAction = authorizedActions[0];
        ProjectReleaseLineControllerAuthorization? lineControllerAuthorization = null;
        if (authorizedAction.LineControllerAuthorizationId is not null)
        {
            var authorizationMatches = line.LineControllerAuthorizations
                .Where(authorization => string.Equals(
                        authorization.AuthorizationId,
                        authorizedAction.LineControllerAuthorizationId,
                        StringComparison.Ordinal)
                    && string.Equals(
                        authorization.OperationId,
                        operationRoute.OperationId,
                        StringComparison.Ordinal)
                    && string.Equals(
                        authorization.ActionId,
                        authorizedAction.ActionId,
                        StringComparison.Ordinal)
                    && string.Equals(
                        authorization.ControllerCapabilityId,
                        authorizedAction.RequiredCapability,
                        StringComparison.Ordinal)
                    && string.Equals(
                        authorization.ControllerAction,
                        authorizedAction.CommandName,
                        StringComparison.Ordinal)
                    && string.Equals(request.TargetKind, "Driver", StringComparison.Ordinal)
                    && string.Equals(
                        authorization.TargetBindingId,
                        request.TargetId,
                        StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (authorizationMatches.Length != 1)
            {
                return null;
            }

            lineControllerAuthorization = authorizationMatches[0];
        }
        else if (line.LineControllerAuthorizations.Any(authorization => string.Equals(
                     authorization.OperationId,
                     operationRoute.OperationId,
                     StringComparison.Ordinal)
                 && string.Equals(
                     authorization.ActionId,
                     authorizedAction.ActionId,
                     StringComparison.Ordinal)))
        {
            return null;
        }

        var targetMatches = release.Metadata.TargetReferences.Count(target =>
            string.Equals(target.Kind, request.TargetKind, StringComparison.Ordinal)
            && string.Equals(target.TargetId, request.TargetId, StringComparison.Ordinal));
        if (targetMatches != 1
            || (string.Equals(request.TargetKind, "Capability", StringComparison.Ordinal)
                && !string.Equals(
                    request.TargetId,
                    request.CapabilityId.Value,
                    StringComparison.Ordinal)))
        {
            return null;
        }

        var topologyBinding = lineControllerAuthorization is null
            ? SelectTopologyBinding(request, operationRoute, release.Metadata.CapabilityBindings)
            : release.Metadata.CapabilityBindings.SingleOrDefault(binding =>
                string.Equals(
                    binding.BindingId,
                    lineControllerAuthorization.ControllerBindingId,
                    StringComparison.Ordinal)
                && string.Equals(
                    binding.OwnerSystemId,
                    lineControllerAuthorization.ControllerSystemId,
                    StringComparison.Ordinal)
                && string.Equals(
                    binding.OwnerStationSystemId,
                    operationRoute.StationSystemId,
                    StringComparison.Ordinal)
                && string.Equals(
                    binding.CapabilityId,
                    lineControllerAuthorization.ControllerCapabilityId,
                    StringComparison.Ordinal));
        if (topologyBinding is null)
        {
            return null;
        }

        var releaseScope = new ProjectApplicationWorkspaceScope(
            release.ProjectId,
            release.ApplicationId,
            release.SourceRootPath,
            release.ApplicationProjectRelativePath);
        var projects = await _configurationRepository
            .ListProjectsAsync(releaseScope, cancellationToken)
            .ConfigureAwait(false);
        var configurationSnapshots = projects
            .SelectMany(candidate => candidate.Snapshots)
            .Where(candidate => string.Equals(
                candidate.Id.Value,
                request.ConfigurationSnapshotId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (configurationSnapshots.Length != 1 || !configurationSnapshots[0].IsPublished)
        {
            return null;
        }

        var configurationSnapshot = configurationSnapshots[0];
        var stationProfile = await _configurationRepository
            .GetByIdAsync(
                releaseScope,
                new StationProfileId(configurationSnapshot.StationProfileId.Value),
                cancellationToken)
            .ConfigureAwait(false);
        if (stationProfile is null
            || !string.Equals(
                stationProfile.StationSystemId,
                operationRoute.StationSystemId,
                StringComparison.Ordinal))
        {
            return null;
        }

        var resourceReference = ExternalProgramResourceContract.ReadReference(request.InputPayload);
        if (resourceReference.IsMalformed)
        {
            return null;
        }

        if (resourceReference.ResourceId is not null)
        {
            if (lineControllerAuthorization is not null)
            {
                return null;
            }

            return ResolveExternalProgramRoute(
                request,
                release,
                releaseScope,
                configurationSnapshot,
                operationRoute,
                topologyBinding,
                resourceReference.ResourceId);
        }

        var providerRoute = ResolveProviderRoute(
            request,
            release,
            configurationSnapshot,
            topologyBinding);
        if (lineControllerAuthorization is null)
        {
            return providerRoute;
        }

        return providerRoute is ProjectReleaseDeviceCommandRoute controllerRoute
            ? new ProjectReleaseLineControllerCommandRoute(
                lineControllerAuthorization.AuthorizationId,
                controllerRoute,
                lineControllerAuthorization.TargetStationSystemId,
                lineControllerAuthorization.TargetSystemId,
                lineControllerAuthorization.TargetBindingId,
                lineControllerAuthorization.TargetCapabilityId,
                lineControllerAuthorization.TargetAction)
            : null;
    }

    private static ProjectReleaseExternalProgramCommandRoute? ResolveExternalProgramRoute(
        DeviceCommandRouteRequest request,
        OpenedProjectReleaseArtifact release,
        ProjectApplicationWorkspaceScope releaseScope,
        ConfigurationSnapshot configurationSnapshot,
        ProjectReleaseOperation operation,
        ProjectReleaseCapabilityBinding topologyBinding,
        string requestedResourceId)
    {
        if (!string.Equals(request.TargetKind, "System", StringComparison.Ordinal)
            || !string.Equals(
                request.TargetId,
                operation.StationSystemId,
                StringComparison.Ordinal))
        {
            return null;
        }

        var resources = release.Metadata.ExternalProgramResources
            .Where(candidate => string.Equals(
                candidate.ResourceId,
                requestedResourceId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (resources.Length != 1)
        {
            return null;
        }

        var resource = resources[0];
        long requestTimeoutMilliseconds;
        try
        {
            requestTimeoutMilliseconds = checked(
                request.Timeout.Ticks / TimeSpan.TicksPerMillisecond);
        }
        catch (OverflowException)
        {
            return null;
        }

        if (!string.Equals(resource.CapabilityId, request.CapabilityId.Value, StringComparison.Ordinal)
            || !string.Equals(resource.CommandName, request.CommandName, StringComparison.Ordinal)
            || resource.ExecutionLimits.TimeoutMilliseconds != requestTimeoutMilliseconds)
        {
            return null;
        }

        ProjectReleaseRuntimeCommandRoute? providerRoute = null;
        ProjectReleaseSourceFile? entryPointFile = null;
        var applicationRelativePath = Path.GetRelativePath(
                release.SourceRootPath,
                releaseScope.ApplicationRootPath)
            .Replace('\\', '/');
        var frozenResourcePrefix =
            $"{applicationRelativePath}/{resource.ResourceRelativePath}/";
        var frozenResourceFiles = release.Files
            .Where(file => file.RelativePath.StartsWith(frozenResourcePrefix, StringComparison.Ordinal))
            .Select(file => new ExternalProgramRouteFile(
                file.RelativePath[frozenResourcePrefix.Length..],
                file.SizeBytes,
                file.Sha256))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        if (frozenResourceFiles.Count(file => string.Equals(
                file.RelativePath,
                ExternalProgramResourceContract.DescriptorFileName,
                StringComparison.Ordinal)) != 1
            || resource.Files.Any(file => frozenResourceFiles.Count(candidate => string.Equals(
                candidate.RelativePath,
                file.RelativePath[(resource.ResourceRelativePath.Length + 1)..],
                StringComparison.Ordinal)) != 1)
            || frozenResourceFiles.Length != resource.Files.Count + 1)
        {
            return null;
        }

        if (string.Equals(
                resource.LaunchKind,
                ProjectReleaseExternalProgramLaunchKinds.ApplicationExecutable,
                StringComparison.Ordinal))
        {
            if (resource.EntryPoint is null
                || resource.ProviderKey is not null
                || !string.Equals(
                    topologyBinding.ProviderKind,
                    ProjectReleaseRuntimeProviderKinds.ExternalSystem,
                    StringComparison.Ordinal)
                || !string.Equals(
                    topologyBinding.ProviderKey,
                    resource.ResourceId,
                    StringComparison.Ordinal))
            {
                return null;
            }

            var entryPointRelativePath =
                $"{applicationRelativePath}/{resource.ResourceRelativePath}/{resource.EntryPoint}";
            var entryPointFiles = release.Files
                .Where(file => string.Equals(
                    file.RelativePath,
                    entryPointRelativePath,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (entryPointFiles.Length != 1)
            {
                return null;
            }

            entryPointFile = entryPointFiles[0];
        }
        else if (string.Equals(
                     resource.LaunchKind,
                     ProjectReleaseExternalProgramLaunchKinds.Provider,
                     StringComparison.Ordinal))
        {
            if (resource.EntryPoint is not null
                || resource.ProviderKey is null
                || !string.Equals(
                    topologyBinding.ProviderKey,
                    resource.ProviderKey,
                    StringComparison.Ordinal)
                || topologyBinding.ProviderKind is not (
                    ProjectReleaseRuntimeProviderKinds.ExternalSystem
                    or ProjectReleaseRuntimeProviderKinds.ProcessCommandProvider
                    or ProjectReleaseRuntimeProviderKinds.PluginCommand))
            {
                return null;
            }

            providerRoute = ResolveProviderRoute(
                request,
                release,
                configurationSnapshot,
                topologyBinding);
            if (providerRoute is null)
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        return new ProjectReleaseExternalProgramCommandRoute(
            topologyBinding.ProviderKind,
            topologyBinding.ProviderKey,
            new DeviceCapabilityId(resource.CapabilityId),
            resource.ResourceId,
            resource.LaunchKind,
            releaseScope.ApplicationRootPath,
            resource.ResourceRelativePath,
            release.Metadata.ProductionLine.ProductModel.ProductModelId,
            release.Metadata.ProductionLine.ProductModel.ModelCode,
            release.Metadata.ProductionLine.ProductModel.IdentityInputKey,
            resource.EntryPoint,
            entryPointFile?.SizeBytes,
            entryPointFile?.Sha256,
            frozenResourceFiles,
            resource.ArgumentTemplates.ToArray(),
            resource.InputMappings
                .Select(mapping => new ExternalProgramRouteInputMapping(
                    mapping.Source,
                    mapping.Target))
                .ToArray(),
            resource.ResultMappings
                .Select(mapping => new ExternalProgramRouteResultMapping(
                    mapping.SourcePath,
                    mapping.TargetKey,
                    Enum.Parse<OpenLineOps.Runtime.Contracts.ProductionContextValueKind>(
                        mapping.ValueKind,
                        ignoreCase: false)))
                .ToArray(),
            new ExternalProgramRouteOutcomeMapping(
                resource.OutcomeMapping.SourcePath,
                resource.OutcomeMapping.PassedToken,
                resource.OutcomeMapping.FailedToken,
                resource.OutcomeMapping.AbortedToken),
            new ExternalProgramRoutePermissionProfile(
                resource.PermissionProfile.ProfileName,
                resource.PermissionProfile.NetworkAccessAllowed,
                resource.PermissionProfile.AllowedEnvironmentVariables),
            new ExternalProgramRouteExecutionLimits(
                resource.ExecutionLimits.TimeoutMilliseconds,
                resource.ExecutionLimits.MaximumProcessCount,
                resource.ExecutionLimits.MaximumWorkingSetBytes,
                resource.ExecutionLimits.MaximumCpuTimeMilliseconds,
                resource.ExecutionLimits.MaximumStandardOutputBytes,
                resource.ExecutionLimits.MaximumStandardErrorBytes,
                resource.ExecutionLimits.MaximumArtifactCount,
                resource.ExecutionLimits.MaximumArtifactBytes,
                resource.ExecutionLimits.MaximumTotalArtifactBytes),
            providerRoute);
    }

    private static ProjectReleaseRuntimeCommandRoute? ResolveProviderRoute(
        DeviceCommandRouteRequest request,
        OpenedProjectReleaseArtifact release,
        ConfigurationSnapshot configurationSnapshot,
        ProjectReleaseCapabilityBinding topologyBinding)
    {
        if (string.Equals(
                topologyBinding.ProviderKind,
                ProjectReleaseRuntimeProviderKinds.ProcessCommandProvider,
                StringComparison.Ordinal))
        {
            return new ProjectReleaseProcessCommandRoute(
                topologyBinding.ProviderKey,
                new DeviceCapabilityId(topologyBinding.CapabilityId));
        }

        var deviceBindings = configurationSnapshot.DeviceBindings
            .Where(binding => string.Equals(
                binding.CapabilityId.Value,
                request.CapabilityId.Value,
                StringComparison.Ordinal)
                && string.Equals(
                    binding.OwnerSystemId,
                    topologyBinding.OwnerSystemId,
                    StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (deviceBindings.Length != 1)
        {
            return null;
        }

        var deviceBinding = deviceBindings[0];
        if (string.Equals(
                topologyBinding.ProviderKind,
                ProjectReleaseRuntimeProviderKinds.PluginCommand,
                StringComparison.Ordinal))
        {
            var packageMatches = release.Metadata.PackageDependencies
                .Where(dependency => string.Equals(
                                         dependency.CapabilityId,
                                         topologyBinding.CapabilityId,
                                         StringComparison.Ordinal)
                                     && string.Equals(
                                         dependency.BindingId,
                                         topologyBinding.BindingId,
                                         StringComparison.Ordinal)
                                     && string.Equals(
                                         dependency.ProviderKind,
                                         topologyBinding.ProviderKind,
                                         StringComparison.Ordinal)
                                     && string.Equals(
                                         dependency.ProviderKey,
                                         topologyBinding.ProviderKey,
                                         StringComparison.Ordinal)
                                     && string.Equals(
                                         dependency.OwnerSystemId,
                                         topologyBinding.OwnerSystemId,
                                         StringComparison.Ordinal)
                                     && string.Equals(
                                         dependency.OwnerStationSystemId,
                                         topologyBinding.OwnerStationSystemId,
                                         StringComparison.Ordinal))
                .SelectMany(dependency => dependency.Commands
                    .Where(command => string.Equals(command.Kind, "Device", StringComparison.Ordinal)
                                      && string.Equals(
                                          command.CapabilityId,
                                          request.CapabilityId.Value,
                                          StringComparison.Ordinal)
                                      && string.Equals(
                                          command.CommandName,
                                          request.CommandName,
                                          StringComparison.Ordinal))
                    .Select(command => new { Dependency = dependency, Command = command }))
                .Take(2)
                .ToArray();
            if (packageMatches.Length != 1)
            {
                return null;
            }

            var packageMatch = packageMatches[0];
            return new ProjectReleaseDeviceCommandRoute(
                topologyBinding.ProviderKind,
                topologyBinding.ProviderKey,
                new DeviceInstanceId(deviceBinding.DeviceKey),
                new DeviceCommandDefinitionId(packageMatch.Command.CommandDefinitionId),
                new DeviceCapabilityId(deviceBinding.CapabilityId.Value),
                new DevicePluginPackageIdentity(
                    packageMatch.Dependency.PluginId,
                    packageMatch.Dependency.PackageVersion,
                    packageMatch.Dependency.PackageContentSha256,
                    packageMatch.Dependency.ManifestSha256,
                    packageMatch.Dependency.EntryAssemblySha256,
                    packageMatch.Dependency.ContractVersion,
                    packageMatch.Dependency.RuntimeIdentifier,
                    packageMatch.Dependency.AbiVersion));
        }

        if (!string.Equals(
                topologyBinding.ProviderKind,
                ProjectReleaseRuntimeProviderKinds.Simulator,
                StringComparison.Ordinal)
            && !string.Equals(
                topologyBinding.ProviderKind,
                ProjectReleaseRuntimeProviderKinds.DeviceInstance,
                StringComparison.Ordinal)
            && !string.Equals(
                topologyBinding.ProviderKind,
                ProjectReleaseRuntimeProviderKinds.ExternalSystem,
                StringComparison.Ordinal))
        {
            return null;
        }

        return new ProjectReleaseDeviceCommandRoute(
            topologyBinding.ProviderKind,
            topologyBinding.ProviderKey,
            new DeviceInstanceId(deviceBinding.DeviceKey),
            new DeviceCommandDefinitionId(
                $"{deviceBinding.CapabilityId.Value}:{NormalizeCommandName(request.CommandName)}"),
            new DeviceCapabilityId(deviceBinding.CapabilityId.Value));
    }

    private static string NormalizeCommandName(string commandName)
    {
        return commandName.Replace(" ", "-", StringComparison.Ordinal);
    }

    internal static ProjectReleaseCapabilityBinding? SelectTopologyBinding(
        DeviceCommandRouteRequest request,
        ProjectReleaseOperation operation,
        IReadOnlyCollection<ProjectReleaseCapabilityBinding> bindings)
    {
        var candidates = bindings
            .Where(binding => string.Equals(
                    binding.CapabilityId,
                    request.CapabilityId.Value,
                    StringComparison.Ordinal)
                && string.Equals(
                    binding.OwnerStationSystemId,
                    request.StationSystemId,
                    StringComparison.Ordinal))
            .ToArray();
        IEnumerable<ProjectReleaseCapabilityBinding> selected = request.TargetKind switch
        {
            "System" => candidates.Where(binding => string.Equals(
                binding.OwnerSystemId,
                request.TargetId,
                StringComparison.Ordinal)),
            "Driver" => candidates.Where(binding => string.Equals(
                binding.BindingId,
                request.TargetId,
                StringComparison.Ordinal)),
            "Capability" when candidates.Length == 1 => candidates,
            "Capability" => candidates.Where(binding => operation.Resources.Any(resource =>
                string.Equals(resource.Kind, "Device", StringComparison.Ordinal)
                && string.Equals(resource.Resolution, "Fixed", StringComparison.Ordinal)
                && (string.Equals(
                        resource.TopologyTargetId,
                        binding.BindingId,
                        StringComparison.Ordinal)
                    || string.Equals(
                        resource.TopologyTargetId,
                        binding.OwnerSystemId,
                        StringComparison.Ordinal)))),
            _ => []
        };

        var matches = selected.Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool ResourceFencesMatchFrozenOperation(
        string lineDefinitionId,
        ProjectReleaseOperation operation,
        IReadOnlyCollection<ProjectReleaseLineControllerAuthorization> lineControllerAuthorizations,
        IReadOnlyCollection<DeviceCommandResourceFenceEvidence> evidence)
    {
        if (operation.Resources is null
            || operation.Resources.Count == 0
            || lineControllerAuthorizations is null)
        {
            return false;
        }

        var remaining = evidence.ToList();
        foreach (var resource in operation.Resources)
        {
            var matches = remaining
                .Where(fence => string.Equals(fence.ResourceKind, resource.Kind, StringComparison.Ordinal)
                    && ResourceIdentityMatches(lineDefinitionId, operation, resource, fence.ResourceId))
                .Take(2)
                .ToArray();
            if (matches.Length != 1)
            {
                return false;
            }

            remaining.Remove(matches[0]);
        }

        var remoteResources = lineControllerAuthorizations
            .Where(authorization => string.Equals(
                authorization.OperationId,
                operation.OperationId,
                StringComparison.Ordinal))
            .SelectMany(authorization => new[]
            {
                (Kind: "Station", Id: authorization.TargetStationSystemId),
                (Kind: "Device", Id: authorization.TargetBindingId)
            })
            .Distinct()
            .ToArray();
        foreach (var remoteResource in remoteResources)
        {
            var matches = remaining.Where(fence => string.Equals(
                    fence.ResourceKind,
                    remoteResource.Kind,
                    StringComparison.Ordinal)
                && string.Equals(
                    fence.ResourceId,
                    remoteResource.Id,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (matches.Length != 1)
            {
                return false;
            }

            remaining.Remove(matches[0]);
        }

        return remaining.Count == 0;
    }

    private static bool ResourceIdentityMatches(
        string lineDefinitionId,
        ProjectReleaseOperation operation,
        ProjectReleaseOperationResource resource,
        string actualResourceId)
    {
        if (string.Equals(resource.Resolution, "Fixed", StringComparison.Ordinal))
        {
            var expected = string.Equals(resource.Kind, "Slot", StringComparison.Ordinal)
                ? $"{lineDefinitionId}/{operation.StationSystemId}/{resource.TopologyTargetId}"
                : resource.TopologyTargetId;
            return string.Equals(actualResourceId, expected, StringComparison.Ordinal);
        }

        if (!string.Equals(resource.Kind, "Slot", StringComparison.Ordinal))
        {
            return false;
        }

        var prefix = $"{lineDefinitionId}/{operation.StationSystemId}/";
        if (!actualResourceId.StartsWith(prefix, StringComparison.Ordinal)
            || actualResourceId.Length == prefix.Length
            || actualResourceId[prefix.Length..].Contains('/'))
        {
            return false;
        }

        if (string.Equals(resource.Resolution, "CurrentMaterialSlot", StringComparison.Ordinal))
        {
            return string.Equals(
                resource.TopologyTargetId,
                operation.StationSystemId,
                StringComparison.Ordinal);
        }

        return string.Equals(resource.Resolution, "AvailableSlotInGroup", StringComparison.Ordinal)
            && resource.EligibleSlotIds is not null
            && resource.EligibleSlotIds.Contains(actualResourceId[prefix.Length..], StringComparer.Ordinal);
    }

}
