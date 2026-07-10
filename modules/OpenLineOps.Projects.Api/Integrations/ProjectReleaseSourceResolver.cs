using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Engineering.Application.Configuration;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Processes.Application.Definitions;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Application.Scripting;
using OpenLineOps.Production.Application.Persistence;
using OpenLineOps.Production.Domain.Aggregates;
using OpenLineOps.Production.Domain.Identifiers;
using OpenLineOps.Production.Domain.Models;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Topology.Application.Persistence;
using OpenLineOps.Topology.Application.Topologies;
using OpenLineOps.Topology.Domain.DriverBindings;
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
    private readonly IProjectProductionLineDefinitionRepository _productionRepository;
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
        IProjectProductionLineDefinitionRepository productionRepository,
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
        _productionRepository = productionRepository;
        _flowIrCompiler = flowIrCompiler;
        _flowIrSerializer = flowIrSerializer;
        _clock = clock;
        _blockSources = blockSources?.ToArray() ?? [];
        _packageCatalog = packageCatalog;
    }

    public async Task<Result<ProjectReleaseSourceMetadata>> ResolveAsync(
        ProjectApplicationWorkspaceScope scope,
        string topologyId,
        string productionLineDefinitionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        try
        {
            return await ResolveCoreAsync(
                    scope,
                    topologyId,
                    productionLineDefinitionId,
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
        string productionLineDefinitionId,
        CancellationToken cancellationToken)
    {

        var validation = Validate(topologyId, productionLineDefinitionId);
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

        var discoveredLayoutIds = layouts.Select(layout => layout.Id.Value).ToArray();
        if (discoveredLayoutIds.Distinct(StringComparer.Ordinal).Count() != discoveredLayoutIds.Length
            || discoveredLayoutIds
                .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Distinct(StringComparer.Ordinal).Count() > 1))
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(ApplicationError.Conflict(
                "Projects.ReleaseLayoutIdentityConflict",
                $"Topology {topologyId} contains duplicate or case-conflicting Layout identities."));
        }

        var layoutIds = discoveredLayoutIds.Order(StringComparer.Ordinal).ToArray();
        if (layoutIds.Length == 0)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(ApplicationError.Conflict(
                "Projects.ReleaseLayoutsMissing",
                $"Topology {topologyId} does not have a site layout in application {scope.ApplicationId}."));
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

        ProductionLineDefinition? productionLine;
        try
        {
            productionLine = await _productionRepository
                .GetByIdAsync(
                    scope,
                    new ProductionLineDefinitionId(productionLineDefinitionId),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(ApplicationError.Validation(
                "Projects.ReleaseProductionLineDefinitionIdInvalid",
                exception.Message));
        }

        if (productionLine is null)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(ApplicationError.NotFound(
                "Projects.ReleaseProductionLineNotFound",
                $"Production line {productionLineDefinitionId} was not found in application {scope.ApplicationId}."));
        }

        var productionResult = await ResolveProductionLineAsync(
                scope,
                topology,
                productionLine,
                catalogResult.Value,
                cancellationToken)
            .ConfigureAwait(false);
        if (productionResult.IsFailure)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(productionResult.Error);
        }

        var frozenProduction = productionResult.Value.Metadata;

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

        var blockVersionIds = frozenProduction.Stages
            .SelectMany(stage => stage.BlockVersionIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var packageDependenciesResult = await ResolvePackageDependenciesAsync(
                topology,
                productionResult.Value.FlowDocuments,
                cancellationToken)
            .ConfigureAwait(false);
        if (packageDependenciesResult.IsFailure)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(packageDependenciesResult.Error);
        }

        return Result.Success(new ProjectReleaseSourceMetadata(
            topology.TopologyId,
            layoutIds,
            frozenProduction,
            capabilityBindings,
            targetReferences,
            blockVersionIds,
            packageDependenciesResult.Value));
    }

    private async Task<Result<ResolvedProductionLine>> ResolveProductionLineAsync(
        ProjectApplicationWorkspaceScope scope,
        AutomationTopologyDetails topology,
        ProductionLineDefinition line,
        IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails> blockCatalog,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(line.TopologyId, topology.TopologyId, StringComparison.Ordinal))
        {
            return ProductionFailure(
                "Projects.ReleaseProductionTopologyMismatch",
                $"Production line {line.Id} references topology {line.TopologyId}, not {topology.TopologyId}.");
        }

        foreach (var workstation in line.Workstations)
        {
            var station = topology.Systems.SingleOrDefault(system => string.Equals(
                system.SystemId,
                workstation.StationSystemId,
                StringComparison.Ordinal));
            if (station is null || !string.Equals(station.Kind, "Station", StringComparison.Ordinal))
            {
                return ProductionFailure(
                    "Projects.ReleaseProductionWorkstationInvalid",
                    $"Production workstation {workstation.Id} must reference an existing Station system in topology {topology.TopologyId}.");
            }
        }

        var resolvedFlows = new Dictionary<string, ResolvedProductionFlow>(StringComparer.Ordinal);
        foreach (var flowDefinitionId in line.Stages
                     .Select(stage => stage.FlowDefinitionId)
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            OpenLineOps.Processes.Domain.Definitions.ProcessDefinition? flow;
            try
            {
                flow = await _processRepository
                    .GetByIdAsync(scope, new ProcessDefinitionId(flowDefinitionId), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ArgumentException exception)
            {
                return ProductionFailure(
                    "Projects.ReleaseProductionStageFlowIdInvalid",
                    exception.Message);
            }

            if (flow is null || !flow.IsPublished)
            {
                return ProductionFailure(
                    "Projects.ReleaseProductionStageFlowNotPublished",
                    $"Production line {line.Id} flow {flowDefinitionId} must exist and be published in application {scope.ApplicationId}.");
            }

            var compilation = _flowIrCompiler.Compile(flow, blockCatalog);
            if (compilation.IsFailure)
            {
                return ProductionFailure(
                    "Projects.ReleaseProductionStageFlowCompilationFailed",
                    $"Production line {line.Id} flow {flowDefinitionId} cannot compile to Flow IR: {compilation.Error.Message}");
            }

            var artifact = _flowIrSerializer.Serialize(compilation.Value.Document);
            if (artifact.IsFailure)
            {
                return ProductionFailure(
                    "Projects.ReleaseProductionStageFlowSerializationFailed",
                    $"Production line {line.Id} flow {flowDefinitionId} cannot be serialized canonically: {artifact.Error.Message}");
            }

            resolvedFlows.Add(flowDefinitionId, new(flow, compilation.Value.Document, artifact.Value));
        }

        var engineeringProjects = await _engineeringRepository
            .ListProjectsAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        foreach (var stage in line.Stages.OrderBy(candidate => candidate.Sequence))
        {
            var snapshotMatches = engineeringProjects
                .SelectMany(project => project.Snapshots)
                .Where(snapshot => string.Equals(
                    snapshot.Id.Value,
                    stage.ConfigurationSnapshotId,
                    StringComparison.Ordinal))
                .Select(EngineeringConfigurationMapper.ToDetails)
                .ToArray();
            if (snapshotMatches.Length == 0)
            {
                return ProductionFailure(
                    "Projects.ReleaseProductionStageConfigurationNotFound",
                    $"Production stage {stage.Id} configuration snapshot {stage.ConfigurationSnapshotId} was not found in application {scope.ApplicationId}.");
            }

            if (snapshotMatches.Length > 1)
            {
                return ProductionFailure(
                    "Projects.ReleaseProductionStageConfigurationAmbiguous",
                    $"Production stage {stage.Id} configuration snapshot id {stage.ConfigurationSnapshotId} is used by more than one engineering project in application {scope.ApplicationId}.");
            }

            var resolvedFlow = resolvedFlows[stage.FlowDefinitionId];
            var configurationSnapshot = snapshotMatches[0];
            var configurationValidation = ValidateConfigurationSnapshot(
                ProcessDefinitionMapper.ToDetails(resolvedFlow.Definition),
                configurationSnapshot);
            if (configurationValidation is not null)
            {
                return Result.Failure<ResolvedProductionLine>(configurationValidation);
            }

            var stationProfile = await _engineeringRepository
                .GetByIdAsync(
                    scope,
                    new StationProfileId(configurationSnapshot.StationProfileId),
                    cancellationToken)
                .ConfigureAwait(false);
            if (stationProfile is null)
            {
                return ProductionFailure(
                    "Projects.ReleaseProductionStageStationProfileNotFound",
                    $"Production stage {stage.Id} configuration snapshot {configurationSnapshot.SnapshotId} station profile {configurationSnapshot.StationProfileId} was not found.");
            }

            var workstation = line.Workstations.Single(candidate => candidate.Id == stage.WorkstationId);
            if (!string.Equals(
                    workstation.StationSystemId,
                    stationProfile.StationSystemId,
                    StringComparison.Ordinal))
            {
                return ProductionFailure(
                    "Projects.ReleaseProductionStageStationMismatch",
                    $"Production stage {stage.Id} workstation uses Station system {workstation.StationSystemId}, but configuration snapshot {configurationSnapshot.SnapshotId} uses {stationProfile.StationSystemId}.");
            }

            var bindingValidation = ValidateRequiredCapabilityBindings(
                resolvedFlow.Document,
                stage.FlowDefinitionId,
                topology,
                configurationSnapshot);
            if (bindingValidation is not null)
            {
                return Result.Failure<ResolvedProductionLine>(bindingValidation);
            }
        }

        foreach (var adapter in line.ExternalTestProgramAdapters)
        {
            if (adapter.InputMappings.Any(mapping =>
                    !ProjectReleaseExternalTestProgramContract.IsSupportedInputSource(mapping.Source))
                || adapter.ArgumentTemplates.Any(argument =>
                    !ProjectReleaseExternalTestProgramContract.IsSupportedArgumentTemplate(
                        argument,
                        adapter.InputMappings.Select(mapping => mapping.Target)))
                || adapter.ResultMappings.Any(mapping =>
                    !ProjectReleaseExternalTestProgramContract.IsSupportedResultPath(mapping.SourcePath))
                || !ProjectReleaseExternalTestProgramContract.IsSupportedOutcomeMapping(
                    new ProjectReleaseExternalTestProgramOutcomeMapping(
                        adapter.OutcomeMapping.SourcePath,
                        adapter.OutcomeMapping.PassedToken,
                        adapter.OutcomeMapping.FailedToken,
                        adapter.OutcomeMapping.AbortedToken)))
            {
                return ProductionFailure(
                    "Projects.ReleaseProductionExternalTestContractInvalid",
                    $"External test adapter {adapter.Id} contains an unsupported input source, argument placeholder, or result path.");
            }

            var capability = topology.Capabilities.SingleOrDefault(candidate => string.Equals(
                candidate.CapabilityId,
                adapter.CapabilityId,
                StringComparison.Ordinal));
            var timeoutMilliseconds = checked(adapter.Timeout.Ticks / TimeSpan.TicksPerMillisecond);
            if (capability is null
                || !string.Equals(capability.CommandName, adapter.CommandName, StringComparison.Ordinal)
                || checked(capability.TimeoutSeconds * 1000L) != timeoutMilliseconds)
            {
                return ProductionFailure(
                    "Projects.ReleaseProductionExternalTestCapabilityInvalid",
                    $"External test adapter {adapter.Id} must match one topology capability command and timeout exactly.");
            }

            var bindings = topology.DriverBindings.Where(binding => string.Equals(
                    binding.CapabilityId,
                    adapter.CapabilityId,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (bindings.Length != 1 || !ExternalTestBindingMatches(adapter, bindings[0]))
            {
                return ProductionFailure(
                    "Projects.ReleaseProductionExternalTestProviderInvalid",
                    $"External test adapter {adapter.Id} must match exactly one topology Driver binding.");
            }

            if (adapter.Executable is not null)
            {
                var executablePath = ResolveApplicationFile(scope, adapter.Executable);
                if (!File.Exists(executablePath))
                {
                    return ProductionFailure(
                        "Projects.ReleaseProductionExternalTestExecutableMissing",
                        $"External test executable '{adapter.Executable}' was not found in the portable Application.");
                }
            }
        }

        foreach (var stage in line.Stages)
        {
            if (stage.ExternalTestProgramAdapterId is null)
            {
                continue;
            }

            var adapter = line.ExternalTestProgramAdapters.Single(candidate =>
                candidate.Id == stage.ExternalTestProgramAdapterId);
            var workstation = line.Workstations.Single(candidate => candidate.Id == stage.WorkstationId);
            var station = topology.Systems.Single(candidate => string.Equals(
                candidate.SystemId,
                workstation.StationSystemId,
                StringComparison.Ordinal));
            if (!station.ProvidedCapabilityIds.Contains(adapter.CapabilityId, StringComparer.Ordinal))
            {
                return ProductionFailure(
                    "Projects.ReleaseProductionExternalTestCapabilityNotProvided",
                    $"Production workstation {workstation.Id} Station system does not provide capability {adapter.CapabilityId}.");
            }

            var flow = resolvedFlows[stage.FlowDefinitionId].Document;
            var matchingActions = flow.Nodes
                .SelectMany(node => node.Actions)
                .Where(action => ExternalTestActionMatches(action, adapter, workstation))
                .Take(2)
                .ToArray();
            if (matchingActions.Length != 1)
            {
                return ProductionFailure(
                    "Projects.ReleaseProductionExternalTestActionInvalid",
                    $"Production stage {stage.Id} flow must contain exactly one matching external test action.");
            }
        }

        var metadata = new ProjectReleaseProductionLine(
            line.Id.Value,
            line.DisplayName,
            line.TopologyId,
            new ProjectReleaseDutModel(
                line.DutModel.Id.Value,
                line.DutModel.ModelCode,
                line.DutModel.IdentityInputKey),
            line.Workstations
                .OrderBy(workstation => workstation.Id.Value, StringComparer.Ordinal)
                .Select(workstation => new ProjectReleaseWorkstation(
                    workstation.Id.Value,
                    workstation.DisplayName,
                    workstation.StationSystemId))
                .ToArray(),
            line.Stages
                .OrderBy(stage => stage.Sequence)
                .Select(stage =>
                {
                    var flow = resolvedFlows[stage.FlowDefinitionId];
                    return new ProjectReleaseProductionStage(
                        stage.Id.Value,
                        stage.Sequence,
                        stage.DisplayName,
                        stage.WorkstationId.Value,
                        stage.FlowDefinitionId,
                        stage.ConfigurationSnapshotId,
                        flow.Definition.VersionId.Value,
                        flow.Artifact.SchemaVersion,
                        flow.Artifact.Sha256,
                        flow.Artifact.CanonicalJson,
                        flow.Document.BlockDependencies
                            .Select(dependency => dependency.LockId)
                            .Order(StringComparer.Ordinal)
                            .ToArray(),
                        stage.ExternalTestProgramAdapterId?.Value);
                })
                .ToArray(),
            line.ExternalTestProgramAdapters
                .OrderBy(adapter => adapter.Id.Value, StringComparer.Ordinal)
                .Select(adapter => new ProjectReleaseExternalTestProgramAdapter(
                    adapter.Id.Value,
                    adapter.DisplayName,
                    adapter.CapabilityId,
                    adapter.CommandName,
                    adapter.LaunchKind.ToString(),
                    adapter.Executable,
                    adapter.ProviderKey,
                    adapter.ArgumentTemplates.ToArray(),
                    adapter.InputMappings.Select(mapping =>
                        new ProjectReleaseExternalTestProgramInputMapping(
                            mapping.Source,
                            mapping.Target)).ToArray(),
                    adapter.ResultMappings.Select(mapping =>
                        new ProjectReleaseExternalTestProgramResultMapping(
                            mapping.SourcePath,
                            mapping.TargetKey)).ToArray(),
                    new ProjectReleaseExternalTestProgramOutcomeMapping(
                        adapter.OutcomeMapping.SourcePath,
                        adapter.OutcomeMapping.PassedToken,
                        adapter.OutcomeMapping.FailedToken,
                        adapter.OutcomeMapping.AbortedToken),
                    checked(adapter.Timeout.Ticks / TimeSpan.TicksPerMillisecond)))
                .ToArray());

        return Result.Success(new ResolvedProductionLine(
            metadata,
            resolvedFlows.Values
                .Select(flow => flow.Document)
                .OrderBy(flow => flow.ProcessDefinitionId, StringComparer.Ordinal)
                .ToArray()));
    }

    private static Result<ResolvedProductionLine> ProductionFailure(string code, string message)
    {
        return Result.Failure<ResolvedProductionLine>(ApplicationError.Conflict(code, message));
    }

    private static bool ExternalTestBindingMatches(
        ExternalTestProgramAdapter adapter,
        DriverBindingDetails binding)
    {
        if (adapter.ProviderKey is not null)
        {
            return binding.ProviderKind is "ExternalSystem" or "ProcessCommandProvider" or "PluginCommand"
                && string.Equals(binding.ProviderKey, adapter.ProviderKey, StringComparison.Ordinal);
        }

        return string.Equals(binding.ProviderKind, "ExternalSystem", StringComparison.Ordinal)
            && string.Equals(binding.ProviderKey, adapter.Id.Value, StringComparison.Ordinal);
    }

    private static bool ExternalTestActionMatches(
        FlowIrAction action,
        ExternalTestProgramAdapter adapter,
        WorkstationDefinition workstation)
    {
        var expectedTimeoutMilliseconds = checked(
            adapter.Timeout.Ticks / TimeSpan.TicksPerMillisecond);
        if (action.Kind != FlowIrActionKind.DeviceCommand
            || !string.Equals(action.RequiredCapability, adapter.CapabilityId, StringComparison.Ordinal)
            || !string.Equals(action.CommandName, adapter.CommandName, StringComparison.Ordinal)
            || action.Execution.TimeoutMilliseconds != expectedTimeoutMilliseconds
            || action.Target.Kind != FlowIrTargetReferenceKind.System
            || !string.Equals(action.Target.Reference, workstation.StationSystemId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(action.InputPayload))
        {
            return false;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(action.InputPayload);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return false;
            }

            var adapterProperties = document.RootElement
                .EnumerateObject()
                .Where(property => string.Equals(
                    property.Name,
                    ExternalTestProgramAdapter.InvocationPayloadAdapterIdProperty,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            return adapterProperties.Length == 1
                && adapterProperties[0].Value.ValueKind == System.Text.Json.JsonValueKind.String
                && string.Equals(
                    adapterProperties[0].Value.GetString(),
                    adapter.Id.Value,
                    StringComparison.Ordinal);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static string ResolveApplicationFile(
        ProjectApplicationWorkspaceScope scope,
        string relativePath)
    {
        var applicationRoot = Path.GetFullPath(scope.ApplicationRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(
            applicationRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(applicationRoot + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidDataException(
                $"Application path '{relativePath}' escapes the portable Application.");
        }

        RejectReparsePoint(applicationRoot);
        var current = applicationRoot;
        foreach (var segment in Path.GetRelativePath(applicationRoot, fullPath).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            RejectReparsePoint(current);
        }

        return fullPath;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((Directory.Exists(path) || File.Exists(path))
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Application resource path '{path}' cannot be a symbolic link or reparse point.");
        }
    }

    private sealed record ResolvedProductionFlow(
        OpenLineOps.Processes.Domain.Definitions.ProcessDefinition Definition,
        FlowIrDocument Document,
        FlowIrCanonicalArtifact Artifact);

    private sealed record ResolvedProductionLine(
        ProjectReleaseProductionLine Metadata,
        IReadOnlyCollection<FlowIrDocument> FlowDocuments);

    private static ApplicationError? Validate(
        string topologyId,
        string productionLineDefinitionId)
    {
        if (string.IsNullOrWhiteSpace(topologyId))
        {
            return Required("Projects.TopologyIdRequired", "TopologyId");
        }

        return string.IsNullOrWhiteSpace(productionLineDefinitionId)
            ? Required("Projects.ProductionLineDefinitionIdRequired", "ProductionLineDefinitionId")
            : null;
    }

    private static ApplicationError? ValidateConfigurationSnapshot(
        ProcessDefinitionDetails process,
        ConfigurationSnapshotDetails snapshot)
    {
        if (!string.Equals(snapshot.Status, "Published", StringComparison.Ordinal))
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
        return topology.Systems
            .Select(system => new ProjectReleaseTargetReference("System", system.SystemId))
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
            .Concat(topology.Slots
                .Where(slot => string.Equals(slot.MaterialKind, "Dut", StringComparison.Ordinal))
                .Select(slot => new ProjectReleaseTargetReference("Dut", slot.SlotId)))
            .DistinctBy(target => $"{target.Kind}\u001f{target.TargetId}", StringComparer.Ordinal)
            .OrderBy(target => target.Kind, StringComparer.Ordinal)
            .ThenBy(target => target.TargetId, StringComparer.Ordinal)
            .ToArray();
    }

    internal async Task<Result<IReadOnlyCollection<ProjectReleasePackageDependencyLock>>> ResolvePackageDependenciesAsync(
        AutomationTopologyDetails topology,
        FlowIrDocument flowIr,
        CancellationToken cancellationToken)
    {
        return await ResolvePackageDependenciesAsync(
                topology,
                [flowIr],
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Result<IReadOnlyCollection<ProjectReleasePackageDependencyLock>>> ResolvePackageDependenciesAsync(
        AutomationTopologyDetails topology,
        IReadOnlyCollection<FlowIrDocument> flowDocuments,
        CancellationToken cancellationToken)
    {
        var commandsByCapability = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var action in flowDocuments
                     .SelectMany(flow => flow.Nodes)
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
                commands = new HashSet<string>(StringComparer.Ordinal);
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
                    route.Value.Order(StringComparer.Ordinal).ToArray()));
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
                                        string.Equals(command.CommandName, required, StringComparison.Ordinal))))
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
                    StringComparer.Ordinal))
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
            FlowIrTargetReferenceKind.SlotGroup => topology.SlotGroups.Any(group =>
                string.Equals(group.SlotGroupId, reference, StringComparison.Ordinal)
                && topology.Systems.Any(system => string.Equals(
                        system.SystemId,
                        group.ParentSystemId,
                        StringComparison.Ordinal)
                    && SystemSupportsCapability(system, capabilityId))),
            FlowIrTargetReferenceKind.Slot => topology.Slots.Any(slot =>
                string.Equals(slot.SlotId, reference, StringComparison.Ordinal)
                && topology.Systems.Any(system => string.Equals(
                        system.SystemId,
                        slot.ParentSystemId,
                        StringComparison.Ordinal)
                    && SystemSupportsCapability(system, capabilityId))),
            FlowIrTargetReferenceKind.Driver => topology.DriverBindings.Any(binding =>
                string.Equals(binding.BindingId, reference, StringComparison.Ordinal)
                && string.Equals(binding.CapabilityId, capabilityId, StringComparison.Ordinal)),
            FlowIrTargetReferenceKind.Dut => topology.Slots.Any(slot =>
                string.Equals(slot.SlotId, reference, StringComparison.Ordinal)
                && string.Equals(slot.MaterialKind, "Dut", StringComparison.Ordinal)),
            FlowIrTargetReferenceKind.System => (string.Equals(
                        capabilityId,
                        RuntimeFlowCommand.Capability,
                        StringComparison.Ordinal)
                    && string.Equals(
                        reference,
                        RuntimeFlowCommand.Capability,
                        StringComparison.Ordinal))
                || topology.Systems.Any(system => string.Equals(
                        system.SystemId,
                        reference,
                        StringComparison.Ordinal)
                    && SystemSupportsCapability(system, capabilityId)),
            _ => false
        };
        return targetExists
            ? Result.Success(capabilityId)
            : Result.Failure<string>(ApplicationError.Conflict(
                "Projects.ReleaseFlowIrTargetNotFound",
                $"Flow IR action {action.ActionId} target {action.Target.Kind}/{reference} does not resolve inside topology {topology.TopologyId}."));
    }

    private static bool SystemSupportsCapability(
        AutomationSystemDetails system,
        string capabilityId)
    {
        return system.RequiredCapabilityIds.Contains(capabilityId, StringComparer.Ordinal)
            || system.ProvidedCapabilityIds.Contains(capabilityId, StringComparer.Ordinal);
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

    internal static bool IsPluginProvider(string providerKind)
    {
        return IsDevicePluginProvider(providerKind)
            || string.Equals(
                providerKind,
                nameof(DriverProviderKind.ProcessCommandProvider),
                StringComparison.Ordinal);
    }

    internal static bool IsDevicePluginProvider(string providerKind)
    {
        return string.Equals(
            providerKind,
            nameof(DriverProviderKind.PluginCommand),
            StringComparison.Ordinal);
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
