using System.Text.Json;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Snapshots;
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
            || !string.Equals(line.DutModel.DutModelId, request.DutModelId, StringComparison.Ordinal)
            || !string.Equals(
                line.DutModel.IdentityInputKey,
                request.DutIdentityInputKey,
                StringComparison.Ordinal))
        {
            return null;
        }

        var stageMatches = (
                from stage in line.Stages
                join workstation in line.Workstations
                    on stage.WorkstationId equals workstation.WorkstationId
                where string.Equals(stage.StageId, request.ProductionStageId, StringComparison.Ordinal)
                      && stage.Sequence == request.StageSequence
                      && string.Equals(
                          stage.WorkstationId,
                          request.WorkstationId,
                          StringComparison.Ordinal)
                      && string.Equals(
                          stage.ConfigurationSnapshotId,
                          request.ConfigurationSnapshotId,
                          StringComparison.Ordinal)
                      && string.Equals(
                          workstation.StationSystemId,
                          request.StationId,
                          StringComparison.Ordinal)
                select new { Stage = stage, Workstation = workstation })
            .Take(2)
            .ToArray();
        if (stageMatches.Length != 1)
        {
            return null;
        }

        var stageRoute = stageMatches[0];
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

        var topologyBindings = release.Metadata.CapabilityBindings
            .Where(binding => string.Equals(
                binding.CapabilityId,
                request.CapabilityId.Value,
                StringComparison.Ordinal)
                && (!string.Equals(request.TargetKind, "Driver", StringComparison.Ordinal)
                    || string.Equals(binding.BindingId, request.TargetId, StringComparison.Ordinal)))
            .Take(2)
            .ToArray();
        if (topologyBindings.Length != 1)
        {
            return null;
        }

        var topologyBinding = topologyBindings[0];
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
                stageRoute.Workstation.StationSystemId,
                StringComparison.Ordinal))
        {
            return null;
        }

        var adapterMarker = ReadExternalTestProgramAdapterId(request.InputPayload);
        if (adapterMarker.IsInvalid)
        {
            return null;
        }

        if (adapterMarker.AdapterId is not null)
        {
            return ResolveExternalTestProgramRoute(
                request,
                release,
                releaseScope,
                configurationSnapshot,
                stageRoute.Stage,
                stageRoute.Workstation,
                topologyBinding,
                adapterMarker.AdapterId);
        }

        return ResolveProviderRoute(request, release, configurationSnapshot, topologyBinding);
    }

    private static ProjectReleaseExternalTestProgramCommandRoute? ResolveExternalTestProgramRoute(
        DeviceCommandRouteRequest request,
        OpenedProjectReleaseArtifact release,
        ProjectApplicationWorkspaceScope releaseScope,
        ConfigurationSnapshot configurationSnapshot,
        ProjectReleaseProductionStage stage,
        ProjectReleaseWorkstation workstation,
        ProjectReleaseCapabilityBinding topologyBinding,
        string requestedAdapterId)
    {
        if (!string.Equals(
                stage.ExternalTestProgramAdapterId,
                requestedAdapterId,
                StringComparison.Ordinal)
            || !string.Equals(request.TargetKind, "System", StringComparison.Ordinal)
            || !string.Equals(
                request.TargetId,
                workstation.StationSystemId,
                StringComparison.Ordinal))
        {
            return null;
        }

        var adapters = release.Metadata.ProductionLine.ExternalTestProgramAdapters
            .Where(candidate => string.Equals(
                candidate.AdapterId,
                requestedAdapterId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (adapters.Length != 1)
        {
            return null;
        }

        var adapter = adapters[0];
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

        if (!string.Equals(adapter.CapabilityId, request.CapabilityId.Value, StringComparison.Ordinal)
            || !string.Equals(adapter.CommandName, request.CommandName, StringComparison.Ordinal)
            || adapter.TimeoutMilliseconds != requestTimeoutMilliseconds)
        {
            return null;
        }

        ProjectReleaseRuntimeCommandRoute? providerRoute = null;
        ProjectReleaseSourceFile? executableFile = null;
        if (string.Equals(
                adapter.LaunchKind,
                ProjectReleaseExternalTestProgramLaunchKinds.ApplicationExecutable,
                StringComparison.Ordinal))
        {
            if (adapter.Executable is null
                || adapter.ProviderKey is not null
                || !string.Equals(
                    topologyBinding.ProviderKind,
                    ProjectReleaseRuntimeProviderKinds.ExternalSystem,
                    StringComparison.Ordinal)
                || !string.Equals(
                    topologyBinding.ProviderKey,
                    adapter.AdapterId,
                    StringComparison.Ordinal))
            {
                return null;
            }

            var applicationRelativePath = Path.GetRelativePath(
                    release.SourceRootPath,
                    releaseScope.ApplicationRootPath)
                .Replace('\\', '/');
            var executableRelativePath = $"{applicationRelativePath}/{adapter.Executable}";
            var executableFiles = release.Files
                .Where(file => string.Equals(
                    file.RelativePath,
                    executableRelativePath,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (executableFiles.Length != 1)
            {
                return null;
            }

            executableFile = executableFiles[0];
        }
        else if (string.Equals(
                     adapter.LaunchKind,
                     ProjectReleaseExternalTestProgramLaunchKinds.Provider,
                     StringComparison.Ordinal))
        {
            if (adapter.Executable is not null
                || adapter.ProviderKey is null
                || !string.Equals(
                    topologyBinding.ProviderKey,
                    adapter.ProviderKey,
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

        return new ProjectReleaseExternalTestProgramCommandRoute(
            topologyBinding.ProviderKind,
            topologyBinding.ProviderKey,
            new DeviceCapabilityId(adapter.CapabilityId),
            adapter.AdapterId,
            adapter.LaunchKind,
            releaseScope.ApplicationRootPath,
            release.Metadata.ProductionLine.DutModel.DutModelId,
            release.Metadata.ProductionLine.DutModel.ModelCode,
            release.Metadata.ProductionLine.DutModel.IdentityInputKey,
            adapter.Executable,
            executableFile?.SizeBytes,
            executableFile?.Sha256,
            adapter.ArgumentTemplates.ToArray(),
            adapter.InputMappings
                .Select(mapping => new ExternalTestProgramRouteInputMapping(
                    mapping.Source,
                    mapping.Target))
                .ToArray(),
            adapter.ResultMappings
                .Select(mapping => new ExternalTestProgramRouteResultMapping(
                    mapping.SourcePath,
                    mapping.TargetKey))
                .ToArray(),
            new ExternalTestProgramRouteOutcomeMapping(
                adapter.OutcomeMapping.SourcePath,
                adapter.OutcomeMapping.PassedToken,
                adapter.OutcomeMapping.FailedToken,
                adapter.OutcomeMapping.AbortedToken),
            adapter.TimeoutMilliseconds,
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

    private static ExternalTestProgramAdapterMarker ReadExternalTestProgramAdapterId(
        string? inputPayload)
    {
        if (inputPayload is null)
        {
            return ExternalTestProgramAdapterMarker.None;
        }

        try
        {
            using var document = JsonDocument.Parse(inputPayload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ExternalTestProgramAdapterMarker.None;
            }

            var properties = document.RootElement
                .EnumerateObject()
                .Where(property => string.Equals(
                    property.Name,
                    ProjectReleaseExternalTestProgramContract.AdapterIdProperty,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (properties.Length == 0)
            {
                return ExternalTestProgramAdapterMarker.None;
            }

            if (properties.Length != 1 || properties[0].Value.ValueKind != JsonValueKind.String)
            {
                return ExternalTestProgramAdapterMarker.Invalid;
            }

            var value = properties[0].Value.GetString();
            return string.IsNullOrWhiteSpace(value)
                   || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
                ? ExternalTestProgramAdapterMarker.Invalid
                : new ExternalTestProgramAdapterMarker(value, IsInvalid: false);
        }
        catch (JsonException)
        {
            return ExternalTestProgramAdapterMarker.None;
        }
    }

    private static string NormalizeCommandName(string commandName)
    {
        return commandName.Replace(" ", "-", StringComparison.Ordinal);
    }

    private readonly record struct ExternalTestProgramAdapterMarker(
        string? AdapterId,
        bool IsInvalid)
    {
        public static ExternalTestProgramAdapterMarker None => new(null, IsInvalid: false);

        public static ExternalTestProgramAdapterMarker Invalid => new(null, IsInvalid: true);
    }
}
