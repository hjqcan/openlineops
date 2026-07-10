using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Projects.Application.Persistence;
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
        if (snapshot is null)
        {
            return null;
        }

        var application = project.Applications.SingleOrDefault(candidate =>
            string.Equals(candidate.Id.Value, request.ApplicationId, StringComparison.Ordinal));
        if (application is null)
        {
            return null;
        }

        var scope = new ProjectApplicationWorkspaceScope(
            project.Id.Value,
            request.ApplicationId,
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

        if (release is null
            || !string.Equals(
                release.Metadata.ConfigurationSnapshotId,
                request.ConfigurationSnapshotId,
                StringComparison.Ordinal))
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

        var resolvedTopologyBindings = release.Metadata.CapabilityBindings
            .Where(binding => string.Equals(
                binding.CapabilityId,
                request.CapabilityId.Value,
                StringComparison.Ordinal)
                && (!string.Equals(request.TargetKind, "Driver", StringComparison.Ordinal)
                    || string.Equals(binding.BindingId, request.TargetId, StringComparison.Ordinal)))
            .Take(2)
            .ToArray();
        if (resolvedTopologyBindings.Length != 1)
        {
            return null;
        }

        var topologyBinding = resolvedTopologyBindings[0];

        var releaseScope = new ProjectApplicationWorkspaceScope(
            release.ProjectId,
            release.ApplicationId,
            release.SourceRootPath,
            release.ApplicationProjectRelativePath);
        var projects = await _configurationRepository
            .ListProjectsAsync(releaseScope, cancellationToken)
            .ConfigureAwait(false);
        var configurationSnapshots = projects
            .SelectMany(project => project.Snapshots)
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

        var stationProfile = await _configurationRepository
            .GetByIdAsync(
                releaseScope,
                new StationProfileId(configurationSnapshots[0].StationProfileId.Value),
                cancellationToken)
            .ConfigureAwait(false);
        if (stationProfile is null
            || !string.Equals(stationProfile.StationSystemId, request.StationId, StringComparison.Ordinal)
            || !string.Equals(release.Metadata.StationSystemId, request.StationId, StringComparison.Ordinal))
        {
            return null;
        }

        if (string.Equals(
                topologyBinding.ProviderKind,
                ProjectReleaseRuntimeProviderKinds.ProcessCommandProvider,
                StringComparison.Ordinal))
        {
            return new ProjectReleaseProcessCommandRoute(
                topologyBinding.ProviderKey,
                new DeviceCapabilityId(topologyBinding.CapabilityId));
        }

        var deviceBindings = configurationSnapshots[0].DeviceBindings
            .Where(binding => string.Equals(
                binding.CapabilityId.Value,
                request.CapabilityId.Value,
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
                                          StringComparison.OrdinalIgnoreCase))
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
}
