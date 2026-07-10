using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Engineering.Application.Configuration;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Processes.Application.Definitions;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Application.Scripting;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Topology.Application.Persistence;
using OpenLineOps.Topology.Application.Topologies;
using OpenLineOps.Topology.Domain.Identifiers;
using ProcessDefinitionId = OpenLineOps.Processes.Domain.Identifiers.ProcessDefinitionId;

namespace OpenLineOps.Projects.Api.Integrations;

public sealed class ProjectReleaseSourceResolver : IProjectReleaseSourceResolver
{
    private readonly IProjectAutomationTopologyRepository _topologyRepository;
    private readonly IProjectSiteLayoutRepository _layoutRepository;
    private readonly IProjectProcessDefinitionRepository _processRepository;
    private readonly IProjectEngineeringConfigurationRepository _engineeringRepository;
    private readonly IProjectProcessBlocklyBlockDefinitionRepository _blockRepository;
    private readonly IProcessFlowIrCompiler _flowIrCompiler;
    private readonly IFlowIrCanonicalSerializer _flowIrSerializer;
    private readonly IClock _clock;
    private readonly IProcessBlocklyBlockCatalogSource[] _blockSources;
    private readonly IPluginPackageCatalog? _packageCatalog;

    public ProjectReleaseSourceResolver(
        IProjectAutomationTopologyRepository topologyRepository,
        IProjectSiteLayoutRepository layoutRepository,
        IProjectProcessDefinitionRepository processRepository,
        IProjectEngineeringConfigurationRepository engineeringRepository,
        IProjectProcessBlocklyBlockDefinitionRepository blockRepository,
        IProcessFlowIrCompiler flowIrCompiler,
        IFlowIrCanonicalSerializer flowIrSerializer,
        IClock clock,
        IEnumerable<IProcessBlocklyBlockCatalogSource>? blockSources = null,
        IPluginPackageCatalog? packageCatalog = null)
    {
        _topologyRepository = topologyRepository;
        _layoutRepository = layoutRepository;
        _processRepository = processRepository;
        _engineeringRepository = engineeringRepository;
        _blockRepository = blockRepository;
        _flowIrCompiler = flowIrCompiler;
        _flowIrSerializer = flowIrSerializer;
        _clock = clock;
        _blockSources = blockSources?.ToArray() ?? [];
        _packageCatalog = packageCatalog;
    }

    public async Task<Result<ProjectReleaseSourceMetadata>> ResolveAsync(
        ProjectApplicationWorkspaceScope scope,
        string topologyId,
        string processDefinitionId,
        string configurationSnapshotId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        try
        {
            return await ResolveCoreAsync(
                    scope,
                    topologyId,
                    processDefinitionId,
                    configurationSnapshotId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsSourceStorageException(exception))
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(ApplicationError.Validation(
                "Projects.ReleaseSourceInvalid",
                exception.Message));
        }
    }

    private async Task<Result<ProjectReleaseSourceMetadata>> ResolveCoreAsync(
        ProjectApplicationWorkspaceScope scope,
        string topologyId,
        string processDefinitionId,
        string configurationSnapshotId,
        CancellationToken cancellationToken)
    {

        var validation = Validate(topologyId, processDefinitionId, configurationSnapshotId);
        if (validation is not null)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(validation);
        }

        OpenLineOps.Topology.Domain.Topology.AutomationTopology? topologyAggregate;
        try
        {
            topologyAggregate = await _topologyRepository
                .GetByIdAsync(scope, new AutomationTopologyId(topologyId), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(ApplicationError.Validation(
                "Projects.ReleaseTopologyIdInvalid",
                exception.Message));
        }

        if (topologyAggregate is null)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(ApplicationError.NotFound(
                "Projects.ReleaseTopologyNotFound",
                $"Topology {topologyId} was not found in application {scope.ApplicationId}."));
        }

        var topology = AutomationTopologyMapper.ToDetails(topologyAggregate);

        IReadOnlyCollection<OpenLineOps.Topology.Domain.Layouts.SiteLayout> layouts;
        try
        {
            layouts = await _layoutRepository
                .ListByTopologyAsync(
                    scope,
                    new AutomationTopologyId(topologyId),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(ApplicationError.Validation(
                "Projects.ReleaseTopologyIdInvalid",
                exception.Message));
        }
        catch (InvalidDataException exception)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(ApplicationError.Validation(
                "Projects.ReleaseLayoutSourceInvalid",
                exception.Message));
        }

        var layoutIds = layouts
            .Select(layout => layout.Id.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (layoutIds.Length == 0)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(ApplicationError.Conflict(
                "Projects.ReleaseLayoutsMissing",
                $"Topology {topologyId} does not have a site layout in application {scope.ApplicationId}."));
        }

        OpenLineOps.Processes.Domain.Definitions.ProcessDefinition? processAggregate;
        try
        {
            processAggregate = await _processRepository
                .GetByIdAsync(scope, new ProcessDefinitionId(processDefinitionId), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(ApplicationError.Validation(
                "Projects.ReleaseProcessDefinitionIdInvalid",
                exception.Message));
        }

        if (processAggregate is null)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(ApplicationError.NotFound(
                "Projects.ReleaseProcessDefinitionNotFound",
                $"Process definition {processDefinitionId} was not found in application {scope.ApplicationId}."));
        }

        var process = ProcessDefinitionMapper.ToDetails(processAggregate);
        if (!string.Equals(process.Status, "Published", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(ApplicationError.Conflict(
                "Projects.ReleaseProcessNotPublished",
                $"Process definition {process.ProcessDefinitionId} must be published before a project release can be created."));
        }

        var catalog = new ProcessBlocklyBlockCatalog(
            new ScopedBlockRepository(scope, _blockRepository),
            _clock,
            _blockSources);
        var catalogResult = await catalog
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (catalogResult.IsFailure)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(catalogResult.Error);
        }

        var flowIrCompilationResult = _flowIrCompiler.Compile(
            processAggregate,
            catalogResult.Value);
        if (flowIrCompilationResult.IsFailure)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(ApplicationError.Conflict(
                "Projects.ReleaseFlowIrCompilationFailed",
                $"Process definition {process.ProcessDefinitionId} could not be compiled to Flow IR: {flowIrCompilationResult.Error.Message}"));
        }

        var flowIrArtifactResult = _flowIrSerializer.Serialize(flowIrCompilationResult.Value.Document);
        if (flowIrArtifactResult.IsFailure)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(ApplicationError.Conflict(
                "Projects.ReleaseFlowIrSerializationFailed",
                $"Compiled Flow IR for process definition {process.ProcessDefinitionId} is invalid: {flowIrArtifactResult.Error.Message}"));
        }

        var flowIrArtifact = flowIrArtifactResult.Value;

        var engineeringProjects = await _engineeringRepository
            .ListProjectsAsync(scope, cancellationToken)
            .ConfigureAwait(false);

        var snapshotMatches = engineeringProjects
            .SelectMany(project => project.Snapshots)
            .Where(snapshot => string.Equals(
                snapshot.Id.Value,
                configurationSnapshotId,
                StringComparison.Ordinal))
            .Select(EngineeringConfigurationMapper.ToDetails)
            .ToArray();
        if (snapshotMatches.Length == 0)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(ApplicationError.NotFound(
                "Projects.ReleaseConfigurationSnapshotNotFound",
                $"Published configuration snapshot {configurationSnapshotId} was not found in application {scope.ApplicationId}."));
        }

        if (snapshotMatches.Length > 1)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(ApplicationError.Conflict(
                "Projects.ReleaseConfigurationSnapshotAmbiguous",
                $"Configuration snapshot id {configurationSnapshotId} is used by more than one engineering project in application {scope.ApplicationId}."));
        }

        var configurationSnapshot = snapshotMatches[0];
        var snapshotValidation = ValidateConfigurationSnapshot(process, configurationSnapshot);
        if (snapshotValidation is not null)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(snapshotValidation);
        }

        var bindingValidation = ValidateRequiredCapabilityBindings(
            flowIrCompilationResult.Value.Document,
            process.ProcessDefinitionId,
            topology,
            configurationSnapshot);
        if (bindingValidation is not null)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(bindingValidation);
        }

        var capabilityBindings = topology.DriverBindings
            .OrderBy(binding => binding.CapabilityId, StringComparer.Ordinal)
            .ThenBy(binding => binding.BindingId, StringComparer.Ordinal)
            .Select(binding => new ProjectReleaseCapabilityBinding(
                binding.CapabilityId,
                binding.BindingId,
                binding.ProviderKind,
                binding.ProviderKey))
            .ToArray();
        if (capabilityBindings.Length == 0)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(ApplicationError.Conflict(
                "Projects.ReleaseCapabilityBindingsMissing",
                $"Topology {topology.TopologyId} does not contain a driver binding."));
        }

        var targetReferences = CreateTargetReferences(topology);
        if (targetReferences.Length == 0)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(ApplicationError.Conflict(
                "Projects.ReleaseTargetsMissing",
                $"Topology {topology.TopologyId} does not contain a runtime target."));
        }

        var blockVersionIds = flowIrCompilationResult.Value.Document.BlockDependencies
            .Select(dependency => dependency.LockId)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var packageDependenciesResult = await ResolvePackageDependenciesAsync(
                topology,
                flowIrCompilationResult.Value.Document,
                cancellationToken)
            .ConfigureAwait(false);
        if (packageDependenciesResult.IsFailure)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(packageDependenciesResult.Error);
        }

        return Result.Success(new ProjectReleaseSourceMetadata(
            topology.TopologyId,
            layoutIds,
            process.ProcessDefinitionId,
            process.VersionId,
            flowIrArtifact.SchemaVersion,
            flowIrArtifact.Sha256,
            flowIrArtifact.CanonicalJson,
            configurationSnapshot.SnapshotId,
            capabilityBindings,
            targetReferences,
            blockVersionIds,
            packageDependenciesResult.Value));
    }

    private static ApplicationError? Validate(
        string topologyId,
        string processDefinitionId,
        string configurationSnapshotId)
    {
        if (string.IsNullOrWhiteSpace(topologyId))
        {
            return Required("Projects.TopologyIdRequired", "TopologyId");
        }

        if (string.IsNullOrWhiteSpace(processDefinitionId))
        {
            return Required("Projects.ProcessDefinitionIdRequired", "ProcessDefinitionId");
        }

        return string.IsNullOrWhiteSpace(configurationSnapshotId)
            ? Required("Projects.ConfigurationSnapshotIdRequired", "ConfigurationSnapshotId")
            : null;
    }

    private static ApplicationError? ValidateConfigurationSnapshot(
        ProcessDefinitionDetails process,
        ConfigurationSnapshotDetails snapshot)
    {
        if (!string.Equals(snapshot.Status, "Published", StringComparison.OrdinalIgnoreCase))
        {
            return ApplicationError.Conflict(
                "Projects.ReleaseConfigurationSnapshotNotPublished",
                $"Configuration snapshot {snapshot.SnapshotId} is not published.");
        }

        if (!string.Equals(
                snapshot.ProcessDefinitionId,
                process.ProcessDefinitionId,
                StringComparison.Ordinal))
        {
            return ApplicationError.Conflict(
                "Projects.ReleaseConfigurationProcessMismatch",
                $"Configuration snapshot {snapshot.SnapshotId} belongs to process definition {snapshot.ProcessDefinitionId}, not {process.ProcessDefinitionId}.");
        }

        return !string.Equals(snapshot.ProcessVersionId, process.VersionId, StringComparison.Ordinal)
            ? ApplicationError.Conflict(
                "Projects.ReleaseConfigurationProcessVersionMismatch",
                $"Configuration snapshot {snapshot.SnapshotId} references process version {snapshot.ProcessVersionId}, not {process.VersionId}.")
            : null;
    }

    private static ApplicationError? ValidateRequiredCapabilityBindings(
        FlowIrDocument flowIr,
        string processDefinitionId,
        AutomationTopologyDetails topology,
        ConfigurationSnapshotDetails configurationSnapshot)
    {
        var requiredActions = flowIr.Nodes
            .SelectMany(node => node.Actions)
            .Where(action => action.Kind == FlowIrActionKind.DeviceCommand)
            .Where(action => !(action.Target.Kind == FlowIrTargetReferenceKind.System
                && string.Equals(action.RequiredCapability, RuntimeFlowCommand.Capability, StringComparison.Ordinal)))
            .OrderBy(action => action.ActionId, StringComparer.Ordinal)
            .ToArray();

        var declaredCapabilities = topology.Capabilities
            .Select(capability => capability.CapabilityId)
            .ToHashSet(StringComparer.Ordinal);
        var configurationBindings = configurationSnapshot.DeviceBindings
            .Select(binding => binding.CapabilityId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var action in requiredActions)
        {
            var capabilityResult = ResolveActionCapabilityTarget(topology, action);
            if (capabilityResult.IsFailure)
            {
                return capabilityResult.Error;
            }

            var capabilityId = capabilityResult.Value;
            if (!declaredCapabilities.Contains(capabilityId))
            {
                return ApplicationError.Conflict(
                    "Projects.ReleaseRequiredCapabilityMissing",
                    $"Process definition {processDefinitionId} requires capability {capabilityId}, but topology {topology.TopologyId} does not declare it.");
            }

            var topologyBindings = topology.DriverBindings
                .Where(binding => string.Equals(binding.CapabilityId, capabilityId, StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (topologyBindings.Length != 1)
            {
                return ApplicationError.Conflict(
                    "Projects.ReleaseDriverBindingMissing",
                    $"Required capability {capabilityId} must have exactly one driver binding in topology {topology.TopologyId}.");
            }

            if (IsDevicePluginProvider(topologyBindings[0].ProviderKind)
                && !configurationBindings.Contains(capabilityId))
            {
                return ApplicationError.Conflict(
                    "Projects.ReleaseDeviceBindingMissing",
                    $"Required capability {capabilityId} does not have a device binding in configuration snapshot {configurationSnapshot.SnapshotId}.");
            }
        }

        return null;
    }

    private static ProjectReleaseTargetReference[] CreateTargetReferences(
        AutomationTopologyDetails topology)
    {
        return topology.Nodes
            .Select(node => new ProjectReleaseTargetReference("EquipmentNode", node.NodeId))
            .Concat(topology.Modules.Select(module => new ProjectReleaseTargetReference(
                "AutomationModule",
                module.ModuleId)))
            .Concat(topology.SlotGroups.Select(group => new ProjectReleaseTargetReference(
                "SlotGroup",
                group.SlotGroupId)))
            .Concat(topology.Slots.Select(slot => new ProjectReleaseTargetReference(
                "Slot",
                slot.SlotId)))
            .Concat(topology.Capabilities.Select(capability => new ProjectReleaseTargetReference(
                "Capability",
                capability.CapabilityId)))
            .Concat(topology.DriverBindings.Select(binding => new ProjectReleaseTargetReference(
                "Driver",
                binding.BindingId)))
            .Concat(topology.Modules.Select(module => new ProjectReleaseTargetReference(
                "System",
                module.ModuleId)))
            .Append(new ProjectReleaseTargetReference("System", topology.TopologyId))
            .Concat(topology.Slots
                .Where(slot => string.Equals(slot.MaterialKind, "Dut", StringComparison.OrdinalIgnoreCase))
                .Select(slot => new ProjectReleaseTargetReference("Dut", slot.SlotId)))
            .DistinctBy(target => $"{target.Kind}\u001f{target.TargetId}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(target => target.Kind, StringComparer.Ordinal)
            .ThenBy(target => target.TargetId, StringComparer.Ordinal)
            .ToArray();
    }

    internal async Task<Result<IReadOnlyCollection<ProjectReleasePackageDependencyLock>>> ResolvePackageDependenciesAsync(
        AutomationTopologyDetails topology,
        FlowIrDocument flowIr,
        CancellationToken cancellationToken)
    {
        var commandsByCapability = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var action in flowIr.Nodes
                     .SelectMany(node => node.Actions)
                     .Where(action => action.Kind == FlowIrActionKind.DeviceCommand))
        {
            var capabilityResult = ResolveActionCapabilityTarget(topology, action);
            if (capabilityResult.IsFailure)
            {
                return Result.Failure<IReadOnlyCollection<ProjectReleasePackageDependencyLock>>(
                    capabilityResult.Error);
            }

            if (action.Target.Kind == FlowIrTargetReferenceKind.System
                && !topology.DriverBindings.Any(binding => string.Equals(
                    binding.CapabilityId,
                    capabilityResult.Value,
                    StringComparison.Ordinal)))
            {
                // Internal system commands (for example runtime.flow result patching)
                // do not resolve through a topology provider package.
                continue;
            }

            if (!commandsByCapability.TryGetValue(capabilityResult.Value, out var commands))
            {
                commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                commandsByCapability.Add(capabilityResult.Value, commands);
            }

            commands.Add(action.CommandName);
        }

        if (commandsByCapability.Count == 0)
        {
            return Result.Success<IReadOnlyCollection<ProjectReleasePackageDependencyLock>>([]);
        }

        var resolvedRoutes = new List<(DriverBindingDetails Binding, string[] CommandNames)>();
        foreach (var route in commandsByCapability.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var bindings = topology.DriverBindings
                .Where(binding => string.Equals(
                    binding.CapabilityId,
                    route.Key,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (bindings.Length != 1)
            {
                return Result.Failure<IReadOnlyCollection<ProjectReleasePackageDependencyLock>>(
                    ApplicationError.Conflict(
                        "Projects.ReleaseFlowIrRouteBindingInvalid",
                        $"Flow IR capability {route.Key} must resolve to exactly one topology driver binding."));
            }

            if (IsPluginProvider(bindings[0].ProviderKind))
            {
                resolvedRoutes.Add((
                    bindings[0],
                    route.Value.Order(StringComparer.OrdinalIgnoreCase).ToArray()));
            }
        }

        if (resolvedRoutes.Count == 0)
        {
            return Result.Success<IReadOnlyCollection<ProjectReleasePackageDependencyLock>>([]);
        }

        if (_packageCatalog is null)
        {
            return Result.Failure<IReadOnlyCollection<ProjectReleasePackageDependencyLock>>(
                ApplicationError.Conflict(
                    "Projects.ReleasePluginPackageCatalogMissing",
                    "A plugin package catalog is required to freeze plugin-backed capability routes."));
        }

        var packages = await _packageCatalog.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var locks = new List<ProjectReleasePackageDependencyLock>(resolvedRoutes.Count);

        foreach (var route in resolvedRoutes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var binding = route.Binding;
            var requiredCommandNames = route.CommandNames;

            var matches = packages
                .Select(package => new
                {
                    Package = package,
                    Commands = GetPackageCommands(package, binding.ProviderKind, binding.CapabilityId, binding.ProviderKey)
                })
                .Where(candidate => candidate.Commands.Length > 0
                                    && requiredCommandNames.All(required => candidate.Commands.Any(command =>
                                        string.Equals(command.CommandName, required, StringComparison.OrdinalIgnoreCase))))
                .Take(2)
                .ToArray();
            if (matches.Length == 0)
            {
                return Result.Failure<IReadOnlyCollection<ProjectReleasePackageDependencyLock>>(
                    ApplicationError.Conflict(
                        "Projects.ReleasePluginPackageMissing",
                        $"Provider {binding.ProviderKind}/{binding.ProviderKey} for capability {binding.CapabilityId} does not resolve to an installed plugin package and command set."));
            }

            if (matches.Length > 1)
            {
                return Result.Failure<IReadOnlyCollection<ProjectReleasePackageDependencyLock>>(
                    ApplicationError.Conflict(
                        "Projects.ReleasePluginPackageAmbiguous",
                        $"Provider {binding.ProviderKind}/{binding.ProviderKey} for capability {binding.CapabilityId} resolves to more than one plugin package."));
            }

            var match = matches[0];
            var integrityError = ValidatePackageIntegrity(match.Package);
            if (integrityError is not null)
            {
                return Result.Failure<IReadOnlyCollection<ProjectReleasePackageDependencyLock>>(
                    ApplicationError.Conflict(
                        "Projects.ReleasePluginPackageIntegrityInvalid",
                        $"Plugin package {match.Package.Manifest.Id} cannot be frozen: {integrityError}"));
            }

            var packageFiles = match.Package.Files!
                .Select(file => new ProjectReleasePackageFile(
                    file.RelativePath,
                    file.SizeBytes,
                    file.Sha256))
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToArray();
            var lockedCommands = match.Commands
                .Where(command => requiredCommandNames.Contains(
                    command.CommandName,
                    StringComparer.OrdinalIgnoreCase))
                .ToArray();
            locks.Add(new ProjectReleasePackageDependencyLock(
                binding.CapabilityId,
                binding.BindingId,
                binding.ProviderKind,
                binding.ProviderKey,
                match.Package.Manifest.Id,
                match.Package.Manifest.Id,
                match.Package.Manifest.Version,
                match.Package.PackageContentSha256,
                match.Package.ManifestSha256,
                match.Package.EntryAssemblySha256,
                match.Package.Manifest.ContractVersion,
                match.Package.Manifest.RuntimeIdentifier,
                match.Package.Manifest.AbiVersion,
                $"packages/{match.Package.PackageContentSha256}",
                match.Package.ManifestRelativePath,
                match.Package.EntryAssemblyRelativePath,
                lockedCommands.Select(command => new ProjectReleasePackageCommandLock(
                        command.Kind,
                        command.CommandDefinitionId,
                        command.CapabilityId,
                        command.CommandName))
                    .OrderBy(command => command.Kind, StringComparer.Ordinal)
                    .ThenBy(command => command.CommandDefinitionId, StringComparer.Ordinal)
                    .ToArray(),
                packageFiles,
                Path.GetFullPath(match.Package.PackagePath)));
        }

        return Result.Success<IReadOnlyCollection<ProjectReleasePackageDependencyLock>>(locks);
    }

    internal static Result<string> ResolveActionCapabilityTarget(
        AutomationTopologyDetails topology,
        FlowIrAction action)
    {
        var capabilityId = action.RequiredCapability?.Trim();
        if (string.IsNullOrWhiteSpace(capabilityId))
        {
            return Result.Failure<string>(ApplicationError.Conflict(
                "Projects.ReleaseFlowIrCapabilityMissing",
                $"Flow IR action {action.ActionId} does not declare a required capability."));
        }

        var reference = action.Target.Reference?.Trim();
        if (string.IsNullOrWhiteSpace(reference))
        {
            return Result.Failure<string>(ApplicationError.Conflict(
                "Projects.ReleaseFlowIrTargetMissing",
                $"Flow IR action {action.ActionId} does not declare a target reference."));
        }

        var targetExists = action.Target.Kind switch
        {
            FlowIrTargetReferenceKind.Capability => string.Equals(
                reference,
                capabilityId,
                StringComparison.Ordinal),
            FlowIrTargetReferenceKind.AutomationModule => topology.Modules.Any(module =>
                string.Equals(module.ModuleId, reference, StringComparison.Ordinal)
                && ModuleSupportsCapability(module, capabilityId)),
            FlowIrTargetReferenceKind.EquipmentNode => topology.Nodes.Any(node =>
                string.Equals(node.NodeId, reference, StringComparison.Ordinal))
                && topology.Modules.Any(module => string.Equals(module.NodeId, reference, StringComparison.Ordinal)
                    && ModuleSupportsCapability(module, capabilityId)),
            FlowIrTargetReferenceKind.SlotGroup => topology.SlotGroups.Any(group =>
                string.Equals(group.SlotGroupId, reference, StringComparison.Ordinal)
                && topology.Modules.Any(module => string.Equals(
                        module.NodeId,
                        group.ParentNodeId,
                        StringComparison.Ordinal)
                    && ModuleSupportsCapability(module, capabilityId))),
            FlowIrTargetReferenceKind.Slot => topology.Slots.Any(slot =>
                string.Equals(slot.SlotId, reference, StringComparison.Ordinal)
                && topology.Modules.Any(module => string.Equals(
                        module.NodeId,
                        slot.ParentNodeId,
                        StringComparison.Ordinal)
                    && ModuleSupportsCapability(module, capabilityId))),
            FlowIrTargetReferenceKind.Driver => topology.DriverBindings.Any(binding =>
                string.Equals(binding.BindingId, reference, StringComparison.Ordinal)
                && string.Equals(binding.CapabilityId, capabilityId, StringComparison.Ordinal)),
            FlowIrTargetReferenceKind.Dut => topology.Slots.Any(slot =>
                string.Equals(slot.SlotId, reference, StringComparison.Ordinal)
                && string.Equals(slot.MaterialKind, "Dut", StringComparison.OrdinalIgnoreCase)),
            FlowIrTargetReferenceKind.System => string.Equals(
                    reference,
                    topology.TopologyId,
                    StringComparison.Ordinal)
                || (string.Equals(
                        capabilityId,
                        RuntimeFlowCommand.Capability,
                        StringComparison.Ordinal)
                    && string.Equals(
                        reference,
                        RuntimeFlowCommand.Capability,
                        StringComparison.Ordinal))
                || topology.Modules.Any(module => string.Equals(
                        module.ModuleId,
                        reference,
                        StringComparison.Ordinal)
                    && ModuleSupportsCapability(module, capabilityId)),
            _ => false
        };
        return targetExists
            ? Result.Success(capabilityId)
            : Result.Failure<string>(ApplicationError.Conflict(
                "Projects.ReleaseFlowIrTargetNotFound",
                $"Flow IR action {action.ActionId} target {action.Target.Kind}/{reference} does not resolve inside topology {topology.TopologyId}."));
    }

    private static bool ModuleSupportsCapability(
        AutomationModuleDetails module,
        string capabilityId)
    {
        return module.RequiredCapabilityIds.Contains(capabilityId, StringComparer.Ordinal)
            || module.ProvidedCapabilityIds.Contains(capabilityId, StringComparer.Ordinal);
    }

    private static ResolvedPackageCommand[] GetPackageCommands(
        PluginPackageDescriptor package,
        string providerKind,
        string capabilityId,
        string providerKey)
    {
        IEnumerable<ResolvedPackageCommand> commands = IsDevicePluginProvider(providerKind)
            ? (package.Manifest.DeviceCommands ?? []).Select(command => new ResolvedPackageCommand(
                "Device",
                command.Id,
                command.Capability,
                command.CommandName))
            : (package.Manifest.ProcessCommands ?? []).Select(command => new ResolvedPackageCommand(
                "Process",
                command.Id,
                command.Capability,
                command.CommandName));
        commands = commands.Where(command =>
            string.Equals(command.CapabilityId, capabilityId, StringComparison.Ordinal));

        if (string.Equals(package.Manifest.Id, providerKey, StringComparison.Ordinal))
        {
            return commands.ToArray();
        }

        return commands
            .Where(command => string.Equals(
                command.CommandDefinitionId,
                providerKey,
                StringComparison.Ordinal))
            .ToArray();
    }

    private static string? ValidatePackageIntegrity(PluginPackageDescriptor package)
    {
        if (string.IsNullOrWhiteSpace(package.Manifest.Id)
            || string.IsNullOrWhiteSpace(package.Manifest.Version)
            || string.IsNullOrWhiteSpace(package.Manifest.ContractVersion)
            || string.IsNullOrWhiteSpace(package.Manifest.RuntimeIdentifier)
            || string.IsNullOrWhiteSpace(package.Manifest.AbiVersion))
        {
            return "identity, version, contract, RID, or ABI metadata is missing.";
        }

        if (!IsSha256(package.PackageContentSha256)
            || !IsSha256(package.ManifestSha256)
            || !IsSha256(package.EntryAssemblySha256))
        {
            return "package, manifest, or entry assembly SHA-256 is missing or invalid.";
        }

        if (package.Files is not { Count: > 0 }
            || string.IsNullOrWhiteSpace(package.ManifestRelativePath)
            || string.IsNullOrWhiteSpace(package.EntryAssemblyRelativePath)
            || string.IsNullOrWhiteSpace(package.PackagePath)
            || !Directory.Exists(package.PackagePath))
        {
            return "package file inventory or source path is missing.";
        }

        if (package.Files.Any(file => file.SizeBytes < 0 || !IsSha256(file.Sha256)))
        {
            return "package file inventory contains an invalid size or SHA-256.";
        }

        var canonical = new StringBuilder();
        foreach (var file in package.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            canonical.Append(file.RelativePath)
                .Append('\0')
                .Append(file.SizeBytes.ToString(CultureInfo.InvariantCulture))
                .Append('\0')
                .Append(file.Sha256)
                .Append('\n');
        }

        var computed = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
        return string.Equals(computed, package.PackageContentSha256, StringComparison.Ordinal)
            ? null
            : "package full-tree SHA-256 does not match its file inventory.";
    }

    private static bool IsPluginProvider(string providerKind)
    {
        return IsDevicePluginProvider(providerKind)
            || string.Equals(providerKind, "ProcessCommandProvider", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDevicePluginProvider(string providerKind)
    {
        return string.Equals(providerKind, "PluginCommand", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha256(string value)
    {
        return value.Length == 64
               && string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal)
               && value.All(Uri.IsHexDigit);
    }

    private sealed record ResolvedPackageCommand(
        string Kind,
        string CommandDefinitionId,
        string CapabilityId,
        string CommandName);

    private static ApplicationError Required(string code, string fieldName)
    {
        return ApplicationError.Validation(code, $"{fieldName} is required.");
    }

    private static bool IsSourceStorageException(Exception exception)
    {
        return exception is InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException;
    }

    private sealed class ScopedBlockRepository : IProcessBlocklyBlockDefinitionRepository
    {
        private readonly ProjectApplicationWorkspaceScope _scope;
        private readonly IProjectProcessBlocklyBlockDefinitionRepository _repository;

        public ScopedBlockRepository(
            ProjectApplicationWorkspaceScope scope,
            IProjectProcessBlocklyBlockDefinitionRepository repository)
        {
            _scope = scope;
            _repository = repository;
        }

        public ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionRecord>> ListLatestAsync(
            CancellationToken cancellationToken = default)
        {
            return _repository.ListLatestAsync(_scope, cancellationToken);
        }

        public ValueTask<ProcessBlocklyBlockDefinitionRecord?> GetLatestAsync(
            string blockType,
            CancellationToken cancellationToken = default)
        {
            return _repository.GetLatestAsync(_scope, blockType, cancellationToken);
        }

        public ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionRecord>> ListVersionsAsync(
            string blockType,
            CancellationToken cancellationToken = default)
        {
            return _repository.ListVersionsAsync(_scope, blockType, cancellationToken);
        }

        public ValueTask<ProcessBlocklyBlockDefinitionRecord> SaveNewVersionAsync(
            string blockType,
            string category,
            string displayName,
            string blocklyJson,
            string executionMode,
            string runtimeActionContractSchemaVersion,
            string runtimeActionContractJson,
            string runtimeActionContractSha256,
            DateTimeOffset recordedAtUtc,
            CancellationToken cancellationToken = default)
        {
            return _repository.SaveNewVersionAsync(
                _scope,
                blockType,
                category,
                displayName,
                blocklyJson,
                executionMode,
                runtimeActionContractSchemaVersion,
                runtimeActionContractJson,
                runtimeActionContractSha256,
                recordedAtUtc,
                cancellationToken);
        }
    }
}
