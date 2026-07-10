using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Plugins.Application.Capabilities;
using OpenLineOps.Plugins.Application.Commands;
using OpenLineOps.Projects.Application.Persistence;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Projects.Domain.Identifiers;
using DeviceCapabilityId = OpenLineOps.Devices.Domain.Identifiers.DeviceCapabilityId;
using DeviceCommandDefinitionId = OpenLineOps.Devices.Domain.Identifiers.DeviceCommandDefinitionId;
using DeviceInstanceId = OpenLineOps.Devices.Domain.Identifiers.DeviceInstanceId;

namespace OpenLineOps.Devices.Infrastructure.Execution;

public sealed class ProjectReleaseDeviceCommandRouteResolver : IDeviceCommandRouteResolver
{
    private readonly IAutomationProjectRepository _projectRepository;
    private readonly IProjectReleaseArtifactStore _releaseStore;
    private readonly IProjectEngineeringConfigurationRepository _configurationRepository;
    private readonly IPluginCapabilityInventory? _capabilityInventory;
    private readonly IPluginDeviceCommandInventory? _commandInventory;

    public ProjectReleaseDeviceCommandRouteResolver(
        IAutomationProjectRepository projectRepository,
        IProjectReleaseArtifactStore releaseStore,
        IProjectEngineeringConfigurationRepository configurationRepository,
        IPluginCapabilityInventory? capabilityInventory = null,
        IPluginDeviceCommandInventory? commandInventory = null)
    {
        _projectRepository = projectRepository;
        _releaseStore = releaseStore;
        _configurationRepository = configurationRepository;
        _capabilityInventory = capabilityInventory;
        _commandInventory = commandInventory;
    }

    public async ValueTask<DeviceCommandRoute?> ResolveAsync(
        DeviceCommandRouteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.HasProjectReleaseIdentity)
        {
            return null;
        }

        var project = await _projectRepository
            .GetByIdAsync(new AutomationProjectId(request.ProjectId!), cancellationToken)
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
            request.ApplicationId!,
            project.ProjectPath,
            application.ProjectFilePath);

        var release = await _releaseStore
            .OpenAsync(scope, snapshot.Id.Value, snapshot.ReleaseContentSha256, cancellationToken)
            .ConfigureAwait(false);
        if (release is null
            || !string.Equals(
                release.Metadata.ConfigurationSnapshotId,
                request.ConfigurationSnapshotId,
                StringComparison.Ordinal))
        {
            return null;
        }

        var resolvedTopologyBindings = release.Metadata.CapabilityBindings
            .Where(binding => string.Equals(
                binding.CapabilityId,
                request.CapabilityId.Value,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (resolvedTopologyBindings.Length != 1)
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
            .SelectMany(project => project.Snapshots)
            .Where(candidate => string.Equals(
                candidate.Id.Value,
                request.ConfigurationSnapshotId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (configurationSnapshots.Length != 1
            || !configurationSnapshots[0].IsPublished
            || !string.Equals(
                configurationSnapshots[0].StationProfileId.Value,
                request.StationId,
                StringComparison.Ordinal))
        {
            return null;
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
        if (_commandInventory is not null)
        {
            var command = await _commandInventory
                .FindDeviceCommandAsync(
                    deviceBinding.CapabilityId.Value,
                    request.CommandName,
                    cancellationToken)
                .ConfigureAwait(false);
            if (command is null)
            {
                return null;
            }

            return new DeviceCommandRoute(
                new DeviceInstanceId(deviceBinding.DeviceKey),
                new DeviceCommandDefinitionId(command.CommandDefinitionId),
                new DeviceCapabilityId(deviceBinding.CapabilityId.Value));
        }

        if (_capabilityInventory is not null
            && !await _capabilityInventory
                .HasCapabilityAsync(deviceBinding.CapabilityId.Value, cancellationToken)
                .ConfigureAwait(false))
        {
            return null;
        }

        return new DeviceCommandRoute(
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
