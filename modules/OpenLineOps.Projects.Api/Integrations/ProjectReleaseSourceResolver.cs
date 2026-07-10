using System.Text.Json;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Engineering.Application.Configuration;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Processes.Application.Definitions;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Application.Scripting;
using OpenLineOps.Projects.Application.Releases;
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

    public ProjectReleaseSourceResolver(
        IProjectAutomationTopologyRepository topologyRepository,
        IProjectSiteLayoutRepository layoutRepository,
        IProjectProcessDefinitionRepository processRepository,
        IProjectEngineeringConfigurationRepository engineeringRepository,
        IProjectProcessBlocklyBlockDefinitionRepository blockRepository,
        IProcessFlowIrCompiler flowIrCompiler,
        IFlowIrCanonicalSerializer flowIrSerializer,
        IClock clock,
        IEnumerable<IProcessBlocklyBlockCatalogSource>? blockSources = null)
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

        var flowIrCompilationResult = _flowIrCompiler.Compile(processAggregate);
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
            process,
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

        var blockVersionsResult = ResolveUsedBlockVersions(process, catalogResult.Value);
        if (blockVersionsResult.IsFailure)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(blockVersionsResult.Error);
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
            blockVersionsResult.Value));
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
        ProcessDefinitionDetails process,
        AutomationTopologyDetails topology,
        ConfigurationSnapshotDetails configurationSnapshot)
    {
        var requiredCapabilities = process.Nodes
            .Select(node => node.RequiredCapability)
            .Where(capability => !string.IsNullOrWhiteSpace(capability))
            .Select(capability => capability!.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var declaredCapabilities = topology.Capabilities
            .Select(capability => capability.CapabilityId)
            .ToHashSet(StringComparer.Ordinal);
        var topologyBindings = topology.DriverBindings
            .Select(binding => binding.CapabilityId)
            .ToHashSet(StringComparer.Ordinal);
        var configurationBindings = configurationSnapshot.DeviceBindings
            .Select(binding => binding.CapabilityId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var capabilityId in requiredCapabilities)
        {
            if (!declaredCapabilities.Contains(capabilityId))
            {
                return ApplicationError.Conflict(
                    "Projects.ReleaseRequiredCapabilityMissing",
                    $"Process definition {process.ProcessDefinitionId} requires capability {capabilityId}, but topology {topology.TopologyId} does not declare it.");
            }

            if (!topologyBindings.Contains(capabilityId))
            {
                return ApplicationError.Conflict(
                    "Projects.ReleaseDriverBindingMissing",
                    $"Required capability {capabilityId} does not have a driver binding in topology {topology.TopologyId}.");
            }

            if (!configurationBindings.Contains(capabilityId))
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
            .DistinctBy(target => $"{target.Kind}\u001f{target.TargetId}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(target => target.Kind, StringComparer.Ordinal)
            .ThenBy(target => target.TargetId, StringComparer.Ordinal)
            .ToArray();
    }

    private static Result<IReadOnlyCollection<string>> ResolveUsedBlockVersions(
        ProcessDefinitionDetails process,
        IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails> catalog)
    {
        var usedBlockTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in process.Nodes.Where(node => string.Equals(
                     node.ScriptEditorMode,
                     "Blockly",
                     StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrWhiteSpace(node.BlocklyWorkspaceJson))
            {
                return Result.Failure<IReadOnlyCollection<string>>(ApplicationError.Validation(
                    "Projects.ReleaseBlocklyWorkspaceMissing",
                    $"Blockly process node {node.NodeId} does not contain a workspace document."));
            }

            try
            {
                using var workspace = JsonDocument.Parse(node.BlocklyWorkspaceJson);
                var workspaceError = CollectWorkspaceBlockTypes(workspace.RootElement, usedBlockTypes);
                if (workspaceError is not null)
                {
                    return Result.Failure<IReadOnlyCollection<string>>(ApplicationError.Validation(
                        "Projects.ReleaseBlocklyWorkspaceInvalid",
                        $"Blockly process node {node.NodeId} {workspaceError}"));
                }
            }
            catch (JsonException exception)
            {
                return Result.Failure<IReadOnlyCollection<string>>(ApplicationError.Validation(
                    "Projects.ReleaseBlocklyWorkspaceInvalid",
                    $"Blockly process node {node.NodeId} contains invalid JSON: {exception.Message}"));
            }
        }

        var blocksByType = catalog
            .GroupBy(block => block.BlockType, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(block => block.Version).First(),
                StringComparer.Ordinal);
        var versions = new List<string>(usedBlockTypes.Count);
        foreach (var blockType in usedBlockTypes.Order(StringComparer.Ordinal))
        {
            if (!blocksByType.TryGetValue(blockType, out var block))
            {
                return Result.Failure<IReadOnlyCollection<string>>(ApplicationError.Conflict(
                    "Projects.ReleaseBlocklyBlockDefinitionMissing",
                    $"Blockly block {blockType} used by process {process.ProcessDefinitionId} is not available in the project application catalog."));
            }

            versions.Add($"{block.BlockType}@{block.Version}");
        }

        return Result.Success<IReadOnlyCollection<string>>(versions);
    }

    private static string? CollectWorkspaceBlockTypes(
        JsonElement root,
        ISet<string> blockTypes)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("blocks", out var blocks)
            || blocks.ValueKind != JsonValueKind.Object)
        {
            return "must contain a Blockly blocks object.";
        }

        if (!blocks.TryGetProperty("blocks", out var blockArray))
        {
            return null;
        }

        if (blockArray.ValueKind != JsonValueKind.Array)
        {
            return "must contain a Blockly blocks array.";
        }

        foreach (var block in blockArray.EnumerateArray())
        {
            CollectNestedBlockTypes(block, blockTypes);
        }

        return null;
    }

    private static void CollectNestedBlockTypes(JsonElement element, ISet<string> blockTypes)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("type", out var typeElement)
                && typeElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(typeElement.GetString()))
            {
                blockTypes.Add(typeElement.GetString()!.Trim());
            }

            foreach (var property in element.EnumerateObject())
            {
                CollectNestedBlockTypes(property.Value, blockTypes);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectNestedBlockTypes(item, blockTypes);
            }
        }
    }

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
            string pythonCodeTemplate,
            DateTimeOffset recordedAtUtc,
            CancellationToken cancellationToken = default)
        {
            return _repository.SaveNewVersionAsync(
                _scope,
                blockType,
                category,
                displayName,
                blocklyJson,
                pythonCodeTemplate,
                recordedAtUtc,
                cancellationToken);
        }
    }
}
