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
using OpenLineOps.Production.Application.LineDefinitions;
using OpenLineOps.Production.Application.Persistence;
using OpenLineOps.Production.Domain.Aggregates;
using OpenLineOps.Production.Domain.Identifiers;
using OpenLineOps.Production.Domain.Models;
using OpenLineOps.Projects.Application.ExternalPrograms;
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
    private readonly IExternalProgramResourceRepository _externalProgramRepository;
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
        IExternalProgramResourceRepository externalProgramRepository,
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
        _externalProgramRepository = externalProgramRepository;
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
            .Select(binding => new ProjectReleaseCapabilityBinding(
                binding.CapabilityId,
                binding.BindingId,
                binding.ProviderKind,
                binding.ProviderKey,
                binding.OwnerSystemId,
                FindOwningStationSystemId(topology, binding.OwnerSystemId)))
            .OrderBy(binding => binding.OwnerSystemId, StringComparer.Ordinal)
            .ThenBy(binding => binding.CapabilityId, StringComparer.Ordinal)
            .ThenBy(binding => binding.BindingId, StringComparer.Ordinal)
            .ThenBy(binding => binding.ProviderKind, StringComparer.Ordinal)
            .ThenBy(binding => binding.ProviderKey, StringComparer.Ordinal)
            .ThenBy(binding => binding.OwnerStationSystemId, StringComparer.Ordinal)
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

        var blockVersionIds = frozenProduction.Operations
            .SelectMany(operation => operation.BlockVersionIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var packageDependenciesResult = await ResolvePackageDependenciesAsync(
                topology,
                frozenProduction.Operations.Select(operation => new PackageFlowRouteScope(
                    operation.StationSystemId,
                    productionResult.Value.FlowDocuments.Single(flow => string.Equals(
                        flow.ProcessDefinitionId,
                        operation.FlowDefinitionId,
                        StringComparison.Ordinal)),
                    frozenProduction.LineControllerAuthorizations
                        .Where(authorization => string.Equals(
                            authorization.OperationId,
                            operation.OperationId,
                            StringComparison.Ordinal))
                        .ToArray())).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        if (packageDependenciesResult.IsFailure)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(packageDependenciesResult.Error);
        }

        var externalProgramsResult = await ResolveExternalProgramsAsync(
                scope,
                topology,
                frozenProduction,
                productionResult.Value.FlowDocuments,
                cancellationToken)
            .ConfigureAwait(false);
        if (externalProgramsResult.IsFailure)
        {
            return Result.Failure<ProjectReleaseSourceMetadata>(externalProgramsResult.Error);
        }

        return Result.Success(new ProjectReleaseSourceMetadata(
            topology.TopologyId,
            layoutIds,
            frozenProduction,
            externalProgramsResult.Value,
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

        foreach (var operation in line.Operations)
        {
            var resourceFailure = OperationResourceTopologyValidator.Validate(operation, topology);
            if (resourceFailure is not null)
            {
                return ProductionFailure(
                    $"Projects.ReleaseProduction{resourceFailure.Code}",
                    resourceFailure.Message);
            }

            foreach (var authorization in line.LineControllerAuthorizations.Where(candidate =>
                         candidate.OperationId == operation.Id))
            {
                var authorizationFailure = OperationResourceTopologyValidator
                    .ValidateLineControllerAuthorization(operation, authorization, topology);
                if (authorizationFailure is not null)
                {
                    return ProductionFailure(
                        $"Projects.ReleaseProduction{authorizationFailure.Code}",
                        authorizationFailure.Message);
                }
            }
        }

        var resolvedFlows = new Dictionary<string, ResolvedProductionFlow>(StringComparer.Ordinal);
        foreach (var flowDefinitionId in line.Operations
                     .Select(operation => operation.FlowDefinitionId)
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
                    "Projects.ReleaseProductionOperationFlowIdInvalid",
                    exception.Message);
            }

            if (flow is null || !flow.IsPublished)
            {
                return ProductionFailure(
                    "Projects.ReleaseProductionOperationFlowNotPublished",
                    $"Production line {line.Id} flow {flowDefinitionId} must exist and be published in application {scope.ApplicationId}.");
            }

            var compilation = _flowIrCompiler.Compile(flow, blockCatalog);
            if (compilation.IsFailure)
            {
                return ProductionFailure(
                    "Projects.ReleaseProductionOperationFlowCompilationFailed",
                    $"Production line {line.Id} flow {flowDefinitionId} cannot compile to Flow IR: {compilation.Error.Message}");
            }

            var artifact = _flowIrSerializer.Serialize(compilation.Value.Document);
            if (artifact.IsFailure)
            {
                return ProductionFailure(
                    "Projects.ReleaseProductionOperationFlowSerializationFailed",
                    $"Production line {line.Id} flow {flowDefinitionId} cannot be serialized canonically: {artifact.Error.Message}");
            }

            resolvedFlows.Add(flowDefinitionId, new(flow, compilation.Value.Document, artifact.Value));
        }

        var engineeringProjects = await _engineeringRepository
            .ListProjectsAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        foreach (var operation in line.Operations.OrderBy(candidate => candidate.Id.Value, StringComparer.Ordinal))
        {
            var snapshotMatches = engineeringProjects
                .SelectMany(project => project.Snapshots)
                .Where(snapshot => string.Equals(
                    snapshot.Id.Value,
                    operation.ConfigurationSnapshotId,
                    StringComparison.Ordinal))
                .Select(EngineeringConfigurationMapper.ToDetails)
                .ToArray();
            if (snapshotMatches.Length == 0)
            {
                return ProductionFailure(
                    "Projects.ReleaseProductionOperationConfigurationNotFound",
                    $"Production operation {operation.Id} configuration snapshot {operation.ConfigurationSnapshotId} was not found in application {scope.ApplicationId}.");
            }

            if (snapshotMatches.Length > 1)
            {
                return ProductionFailure(
                    "Projects.ReleaseProductionOperationConfigurationAmbiguous",
                    $"Production operation {operation.Id} configuration snapshot id {operation.ConfigurationSnapshotId} is used by more than one engineering project in application {scope.ApplicationId}.");
            }

            var resolvedFlow = resolvedFlows[operation.FlowDefinitionId];
            var operationAuthorizations = line.LineControllerAuthorizations
                .Where(authorization => authorization.OperationId == operation.Id)
                .ToArray();
            foreach (var authorization in operationAuthorizations)
            {
                var actionMatches = resolvedFlow.Document.Nodes
                    .SelectMany(node => node.Actions)
                    .Where(action => string.Equals(
                        action.ActionId,
                        authorization.ActionId,
                        StringComparison.Ordinal))
                    .Take(2)
                    .ToArray();
                if (actionMatches.Length != 1
                    || !OperationResourceTopologyValidator.IsLineControllerActionAuthorized(
                        actionMatches[0],
                        authorization))
                {
                    return ProductionFailure(
                        "Projects.ReleaseProductionLineControllerActionMismatch",
                        $"Line Controller authorization {authorization.Id} does not match exactly one frozen DeviceCommand Flow action.");
                }
            }

            var unauthorizedAction = resolvedFlow.Document.Nodes
                .SelectMany(node => node.Actions)
                .FirstOrDefault(action => !OperationResourceTopologyValidator.IsTargetAuthorized(
                    action.Target.Kind.ToString(),
                    action.Target.Reference,
                    operation,
                    topology)
                    && !operationAuthorizations.Any(authorization =>
                        OperationResourceTopologyValidator.IsLineControllerActionAuthorized(
                            action,
                            authorization)));
            if (unauthorizedAction is not null)
            {
                return ProductionFailure(
                    "Projects.ReleaseProductionOperationActionUnauthorized",
                    $"Production operation {operation.Id} Flow action {unauthorizedAction.ActionId} target "
                    + $"{unauthorizedAction.Target.Kind}:{unauthorizedAction.Target.Reference} is not authorized by its frozen Station resource scope or one exact Line Controller authorization.");
            }

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
                    "Projects.ReleaseProductionOperationStationProfileNotFound",
                    $"Production operation {operation.Id} configuration snapshot {configurationSnapshot.SnapshotId} station profile {configurationSnapshot.StationProfileId} was not found.");
            }

            if (!string.Equals(
                    operation.StationSystemId,
                    stationProfile.StationSystemId,
                    StringComparison.Ordinal))
            {
                return ProductionFailure(
                    "Projects.ReleaseProductionOperationStationMismatch",
                    $"Production operation {operation.Id} uses Station system {operation.StationSystemId}, but configuration snapshot {configurationSnapshot.SnapshotId} uses {stationProfile.StationSystemId}.");
            }

            var bindingValidation = ValidateRequiredCapabilityBindings(
                resolvedFlow.Document,
                operation.FlowDefinitionId,
                operation,
                operationAuthorizations,
                topology,
                configurationSnapshot);
            if (bindingValidation is not null)
            {
                return Result.Failure<ResolvedProductionLine>(bindingValidation);
            }
        }

        var metadata = new ProjectReleaseProductionLine(
            line.Id.Value,
            line.DisplayName,
            line.TopologyId,
            new ProjectReleaseProductModel(
                line.ProductModel.Id.Value,
                line.ProductModel.ModelCode,
                line.ProductModel.IdentityInputKey),
            line.EntryOperationId.Value,
            line.Operations
                .OrderBy(operation => operation.Id.Value, StringComparer.Ordinal)
                .Select(operation =>
                {
                    var flow = resolvedFlows[operation.FlowDefinitionId];
                    return new ProjectReleaseOperation(
                        operation.Id.Value,
                        operation.DisplayName,
                        operation.StationSystemId,
                        operation.FlowDefinitionId,
                        operation.ConfigurationSnapshotId,
                        flow.Definition.VersionId.Value,
                        flow.Artifact.SchemaVersion,
                        flow.Artifact.Sha256,
                        flow.Artifact.CanonicalJson,
                        flow.Document.BlockDependencies
                            .Select(dependency => dependency.LockId)
                            .Order(StringComparer.Ordinal)
                            .ToArray(),
                        operation.Resources.Select(resource => new ProjectReleaseOperationResource(
                                resource.Id.Value,
                                resource.Kind.ToString(),
                                resource.TopologyTargetId,
                                resource.Resolution.ToString(),
                                resource.Resolution == OperationResourceResolution.AvailableSlotInGroup
                                    ? topology.Slots
                                        .Where(slot => slot.IsEnabled && string.Equals(
                                            slot.SlotGroupId,
                                            resource.TopologyTargetId,
                                            StringComparison.Ordinal))
                                        .Select(slot => slot.SlotId)
                                        .Order(StringComparer.Ordinal)
                                        .ToArray()
                                    : []))
                            .OrderBy(resource => resource.Kind, StringComparer.Ordinal)
                            .ThenBy(resource => resource.TopologyTargetId, StringComparer.Ordinal)
                            .ThenBy(resource => resource.BindingId, StringComparer.Ordinal)
                            .ToArray(),
                        flow.Document.Nodes
                            .SelectMany(node => node.Actions.Select((action, index) => new
                            {
                                Node = node,
                                Action = action,
                                Index = index
                            }))
                            .Select(item => new ProjectReleaseAuthorizedAction(
                                item.Action.ActionId,
                                item.Node.Kind == FlowIrNodeKind.Blockly && item.Index > 0
                                    ? $"{item.Node.NodeId}:block-action:{item.Index + 1}"
                                    : item.Node.NodeId,
                                item.Action.Kind.ToString(),
                                item.Action.RequiredCapability,
                                item.Action.CommandName,
                                item.Action.Target.Kind.ToString(),
                                item.Action.Target.Reference,
                                item.Action.Execution.TimeoutMilliseconds,
                                line.LineControllerAuthorizations.SingleOrDefault(authorization =>
                                    authorization.OperationId == operation.Id
                                    && string.Equals(
                                        authorization.ActionId,
                                        item.Action.ActionId,
                                        StringComparison.Ordinal))?.Id.Value))
                            .OrderBy(action => action.ActionId, StringComparer.Ordinal)
                            .ToArray());
                })
                .ToArray(),
            line.Transitions
                .OrderBy(transition => transition.Id.Value, StringComparer.Ordinal)
                .Select(transition => new ProjectReleaseRouteTransition(
                    transition.Id.Value,
                    transition.SourceOperationId.Value,
                    transition.TargetOperationId.Value,
                    transition.Kind.ToString(),
                    transition.RequiredJudgement?.ToString(),
                    transition.MaxTraversals,
                    transition.ParallelGroupId,
                    transition.OutputCondition?.OutputKey,
                    transition.OutputCondition?.ExpectedValue.Kind.ToString(),
                    transition.OutputCondition?.ExpectedValue.CanonicalValue))
                .ToArray(),
            line.LineControllerAuthorizations
                .OrderBy(authorization => authorization.Id.Value, StringComparer.Ordinal)
                .Select(authorization => new ProjectReleaseLineControllerAuthorization(
                    authorization.Id.Value,
                    authorization.OperationId.Value,
                    authorization.ActionId,
                    authorization.ControllerSystemId,
                    authorization.ControllerBindingId,
                    authorization.ControllerCapabilityId,
                    authorization.ControllerAction,
                    authorization.TargetStationSystemId,
                    authorization.TargetSystemId,
                    authorization.TargetBindingId,
                    authorization.TargetCapabilityId,
                    authorization.TargetAction))
                .ToArray());

        return Result.Success(new ResolvedProductionLine(
            metadata,
            resolvedFlows.Values
                .Select(flow => flow.Document)
                .OrderBy(flow => flow.ProcessDefinitionId, StringComparer.Ordinal)
                .ToArray()));
    }

    private async Task<Result<IReadOnlyCollection<ProjectReleaseExternalProgramResource>>> ResolveExternalProgramsAsync(
        ProjectApplicationWorkspaceScope scope,
        AutomationTopologyDetails topology,
        ProjectReleaseProductionLine productionLine,
        IReadOnlyCollection<FlowIrDocument> flowDocuments,
        CancellationToken cancellationToken)
    {
        var referencedActions = new List<(ProjectReleaseOperation Operation, FlowIrAction Action, string ResourceId)>();
        foreach (var operation in productionLine.Operations)
        {
            var flow = flowDocuments.Single(document => string.Equals(
                document.ProcessDefinitionId,
                operation.FlowDefinitionId,
                StringComparison.Ordinal));
            foreach (var action in flow.Nodes.SelectMany(node => node.Actions))
            {
                var reference = ExternalProgramResourceContract.ReadReference(action.InputPayload);
                if (reference.IsMalformed)
                {
                    return Result.Failure<IReadOnlyCollection<ProjectReleaseExternalProgramResource>>(
                        ApplicationError.Conflict(
                            "Projects.ReleaseExternalProgramReferenceInvalid",
                            $"Flow action {action.ActionId} contains a malformed externalProgramResourceId."));
                }

                if (reference.ResourceId is not null)
                {
                    referencedActions.Add((operation, action, reference.ResourceId));
                }
            }
        }

        var resources = new List<ProjectReleaseExternalProgramResource>();
        foreach (var resourceId in referencedActions
                     .Select(item => item.ResourceId)
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resource = await _externalProgramRepository.GetAsync(scope, resourceId, cancellationToken)
                .ConfigureAwait(false);
            if (resource is null)
            {
                return Result.Failure<IReadOnlyCollection<ProjectReleaseExternalProgramResource>>(
                    ApplicationError.Conflict(
                        "Projects.ReleaseExternalProgramResourceMissing",
                        $"Flow references missing Application external program resource {resourceId}."));
            }

            ExternalProgramResourceValidator.ValidateFrozenResource(resource);
            var actions = referencedActions.Where(item => item.ResourceId == resourceId).ToArray();
            foreach (var item in actions)
            {
                if (item.Action.Kind != FlowIrActionKind.DeviceCommand
                    || !string.Equals(
                        item.Action.RequiredCapability,
                        resource.CapabilityId,
                        StringComparison.Ordinal)
                    || !string.Equals(item.Action.CommandName, resource.CommandName, StringComparison.Ordinal)
                    || item.Action.Execution.TimeoutMilliseconds != resource.ExecutionLimits.TimeoutMilliseconds
                    || item.Action.Target.Kind != FlowIrTargetReferenceKind.System
                    || !string.Equals(
                        item.Action.Target.Reference,
                        item.Operation.StationSystemId,
                        StringComparison.Ordinal))
                {
                    return Result.Failure<IReadOnlyCollection<ProjectReleaseExternalProgramResource>>(
                        ApplicationError.Conflict(
                            "Projects.ReleaseExternalProgramActionContractInvalid",
                            $"Flow action {item.Action.ActionId} does not match external program resource {resourceId} and its Operation Station."));
                }
            }

            var capability = topology.Capabilities.SingleOrDefault(candidate => string.Equals(
                candidate.CapabilityId,
                resource.CapabilityId,
                StringComparison.Ordinal));
            if (capability is null
                || !string.Equals(capability.CommandName, resource.CommandName, StringComparison.Ordinal)
                || checked(capability.TimeoutSeconds * 1000L) != resource.ExecutionLimits.TimeoutMilliseconds)
            {
                return Result.Failure<IReadOnlyCollection<ProjectReleaseExternalProgramResource>>(
                    ApplicationError.Conflict(
                        "Projects.ReleaseExternalProgramCapabilityInvalid",
                        $"External program resource {resourceId} must match one topology capability command and timeout."));
            }

            var invalidStationBinding = actions
                .Select(item => item.Operation.StationSystemId)
                .Distinct(StringComparer.Ordinal)
                .FirstOrDefault(stationSystemId =>
                {
                    var bindings = topology.DriverBindings.Where(binding => string.Equals(
                            binding.CapabilityId,
                            resource.CapabilityId,
                            StringComparison.Ordinal)
                        && IsBindingOwnedByStation(binding, stationSystemId, topology))
                        .Take(2)
                        .ToArray();
                    return bindings.Length != 1
                        || !ExternalProgramBindingMatches(resource, bindings[0]);
                });
            if (invalidStationBinding is not null)
            {
                return Result.Failure<IReadOnlyCollection<ProjectReleaseExternalProgramResource>>(
                    ApplicationError.Conflict(
                        "Projects.ReleaseExternalProgramProviderInvalid",
                        $"External program resource {resourceId} must match exactly one topology Driver binding owned by every referencing Station; {invalidStationBinding} does not."));
            }

            var resourcePath = $"{ExternalProgramResourceContract.ResourceDirectoryName}/{resource.ResourceId}";
            resources.Add(new ProjectReleaseExternalProgramResource(
                resource.ResourceId,
                resource.DisplayName,
                resource.CapabilityId,
                resource.CommandName,
                resource.LaunchKind.ToString(),
                resource.EntryPoint,
                resource.ProviderKind,
                resource.ProviderKey,
                resource.ArgumentTemplates,
                resource.InputMappings.Select(mapping => new ProjectReleaseExternalProgramInputMapping(
                    mapping.Source,
                    mapping.Target)).ToArray(),
                resource.ResultMappings.Select(mapping => new ProjectReleaseExternalProgramResultMapping(
                    mapping.SourcePath,
                    mapping.TargetKey,
                    mapping.ValueKind.ToString())).ToArray(),
                new ProjectReleaseExternalProgramOutcomeMapping(
                    resource.OutcomeMapping.SourcePath,
                    resource.OutcomeMapping.PassedToken,
                    resource.OutcomeMapping.FailedToken,
                    resource.OutcomeMapping.AbortedToken),
                new ProjectReleaseExternalProgramPermissionProfile(
                    resource.PermissionProfile.ProfileName,
                    resource.PermissionProfile.NetworkAccessAllowed,
                    resource.PermissionProfile.AllowedEnvironmentVariables),
                new ProjectReleaseExternalProgramExecutionLimits(
                    resource.ExecutionLimits.TimeoutMilliseconds,
                    resource.ExecutionLimits.MaximumProcessCount,
                    resource.ExecutionLimits.MaximumWorkingSetBytes,
                    resource.ExecutionLimits.MaximumCpuTimeMilliseconds,
                    resource.ExecutionLimits.MaximumStandardOutputBytes,
                    resource.ExecutionLimits.MaximumStandardErrorBytes,
                    resource.ExecutionLimits.MaximumArtifactCount,
                    resource.ExecutionLimits.MaximumArtifactBytes,
                    resource.ExecutionLimits.MaximumTotalArtifactBytes),
                resource.Files.Select(file => new ProjectReleaseExternalProgramFile(
                    $"{resourcePath}/{file.RelativePath}",
                    file.SizeBytes,
                    file.Sha256)).ToArray(),
                resource.ContentSha256,
                resourcePath));
        }

        return Result.Success<IReadOnlyCollection<ProjectReleaseExternalProgramResource>>(resources);
    }

    private static bool ExternalProgramBindingMatches(
        ExternalProgramResource resource,
        DriverBindingDetails binding)
    {
        return resource.LaunchKind switch
        {
            ExternalProgramLaunchKind.ApplicationExecutable =>
                string.Equals(binding.ProviderKind, "ExternalSystem", StringComparison.Ordinal)
                && string.Equals(binding.ProviderKey, resource.ResourceId, StringComparison.Ordinal),
            ExternalProgramLaunchKind.Provider =>
                string.Equals(binding.ProviderKind, resource.ProviderKind, StringComparison.Ordinal)
                && string.Equals(binding.ProviderKey, resource.ProviderKey, StringComparison.Ordinal),
            _ => false
        };
    }

    private static Result<ResolvedProductionLine> ProductionFailure(string code, string message)
    {
        return Result.Failure<ResolvedProductionLine>(ApplicationError.Conflict(code, message));
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
        OperationDefinition operation,
        IReadOnlyCollection<LineControllerAuthorization> lineControllerAuthorizations,
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
        foreach (var action in requiredActions)
        {
            var lineControllerAuthorization = lineControllerAuthorizations.SingleOrDefault(
                authorization => string.Equals(
                    authorization.ActionId,
                    action.ActionId,
                    StringComparison.Ordinal));
            var capabilityResult = lineControllerAuthorization is null
                ? ResolveActionCapabilityTarget(topology, action)
                : Result.Success(lineControllerAuthorization.ControllerCapabilityId);
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
                .Where(binding => IsBindingOwnedByStation(binding, operation.StationSystemId, topology))
                .ToArray();
            var topologyBinding = lineControllerAuthorization is null
                ? SelectActionDriverBinding(action, operation, topologyBindings)
                : topologyBindings.SingleOrDefault(binding => string.Equals(
                    binding.BindingId,
                    lineControllerAuthorization.ControllerBindingId,
                    StringComparison.Ordinal));
            if (topologyBinding is null)
            {
                return ApplicationError.Conflict(
                    "Projects.ReleaseDriverBindingMissing",
                    $"Required capability {capabilityId} must resolve to exactly one Driver binding for action {action.ActionId} in topology {topology.TopologyId}.");
            }

            if (IsDevicePluginProvider(topologyBinding.ProviderKind)
                && configurationSnapshot.DeviceBindings.Count(binding =>
                    string.Equals(binding.CapabilityId, capabilityId, StringComparison.Ordinal)
                    && string.Equals(
                        binding.OwnerSystemId,
                        topologyBinding.OwnerSystemId,
                        StringComparison.Ordinal)) != 1)
            {
                return ApplicationError.Conflict(
                    "Projects.ReleaseDeviceBindingMissing",
                    $"Required capability {capabilityId} for owner System {topologyBinding.OwnerSystemId} does not have exactly one Device binding in configuration snapshot {configurationSnapshot.SnapshotId}.");
            }
        }

        return null;
    }

    private static DriverBindingDetails? SelectActionDriverBinding(
        FlowIrAction action,
        OperationDefinition operation,
        DriverBindingDetails[] candidates)
    {
        IEnumerable<DriverBindingDetails> selected = action.Target.Kind switch
        {
            FlowIrTargetReferenceKind.System => candidates.Where(binding => string.Equals(
                binding.OwnerSystemId,
                action.Target.Reference,
                StringComparison.Ordinal)),
            FlowIrTargetReferenceKind.Driver => candidates.Where(binding => string.Equals(
                binding.BindingId,
                action.Target.Reference,
                StringComparison.Ordinal)),
            FlowIrTargetReferenceKind.Capability when candidates.Length == 1 => candidates,
            FlowIrTargetReferenceKind.Capability => candidates.Where(binding =>
                operation.Resources.Any(resource =>
                    resource.Kind == OperationResourceKind.Device
                    && resource.Resolution == OperationResourceResolution.Fixed
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

    private static bool IsBindingOwnedByStation(
        DriverBindingDetails binding,
        string stationSystemId,
        AutomationTopologyDetails topology)
    {
        return string.Equals(
            FindOwningStationSystemId(topology, binding.OwnerSystemId),
            stationSystemId,
            StringComparison.Ordinal);
    }

    private static string FindOwningStationSystemId(
        AutomationTopologyDetails topology,
        string ownerSystemId)
    {
        var currentId = ownerSystemId;
        for (var depth = 0; depth <= topology.Systems.Count; depth++)
        {
            var current = topology.Systems.SingleOrDefault(system => string.Equals(
                system.SystemId,
                currentId,
                StringComparison.Ordinal))
                ?? throw new InvalidDataException(
                    $"Driver binding owner System {ownerSystemId} is missing from topology {topology.TopologyId}.");
            if (string.Equals(current.Kind, "Station", StringComparison.Ordinal))
            {
                return current.SystemId;
            }

            currentId = current.ParentSystemId
                ?? throw new InvalidDataException(
                    $"Driver binding owner System {ownerSystemId} does not belong to a Station subtree.");
        }

        throw new InvalidDataException(
            $"Driver binding owner System {ownerSystemId} has a cyclic topology ancestry.");
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
                .Where(slot => string.Equals(slot.MaterialKind, "ProductionUnit", StringComparison.Ordinal))
                .Select(slot => new ProjectReleaseTargetReference("ProductionUnit", slot.SlotId)))
            .DistinctBy(target => $"{target.Kind}\u001f{target.TargetId}", StringComparer.Ordinal)
            .OrderBy(target => target.Kind, StringComparer.Ordinal)
            .ThenBy(target => target.TargetId, StringComparer.Ordinal)
            .ToArray();
    }

    internal async Task<Result<IReadOnlyCollection<ProjectReleasePackageDependencyLock>>> ResolvePackageDependenciesAsync(
        AutomationTopologyDetails topology,
        string stationSystemId,
        FlowIrDocument flowIr,
        CancellationToken cancellationToken)
    {
        return await ResolvePackageDependenciesAsync(
                topology,
                [new PackageFlowRouteScope(stationSystemId, flowIr, [])],
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Result<IReadOnlyCollection<ProjectReleasePackageDependencyLock>>> ResolvePackageDependenciesAsync(
        AutomationTopologyDetails topology,
        IReadOnlyCollection<PackageFlowRouteScope> stationFlows,
        CancellationToken cancellationToken)
    {
        var commandsByRoute = new Dictionary<
            (string StationSystemId, string CapabilityId, string? BindingId),
            HashSet<string>>();
        foreach (var stationFlow in stationFlows)
        {
            foreach (var action in stationFlow.Flow.Nodes
                         .SelectMany(node => node.Actions)
                         .Where(action => action.Kind == FlowIrActionKind.DeviceCommand))
            {
                var lineControllerAuthorization = stationFlow.LineControllerAuthorizations
                    .SingleOrDefault(authorization => string.Equals(
                        authorization.ActionId,
                        action.ActionId,
                        StringComparison.Ordinal));
                var capabilityResult = lineControllerAuthorization is null
                    ? ResolveActionCapabilityTarget(topology, action)
                    : Result.Success(lineControllerAuthorization.ControllerCapabilityId);
                if (capabilityResult.IsFailure)
                {
                    return Result.Failure<IReadOnlyCollection<ProjectReleasePackageDependencyLock>>(
                        capabilityResult.Error);
                }

                if (action.Target.Kind == FlowIrTargetReferenceKind.System
                    && !topology.DriverBindings.Any(binding => string.Equals(
                            binding.CapabilityId,
                            capabilityResult.Value,
                            StringComparison.Ordinal)
                        && IsBindingOwnedByStation(
                            binding,
                            stationFlow.StationSystemId,
                            topology)))
                {
                    // Internal system commands (for example runtime.flow result patching)
                    // do not resolve through a topology provider package.
                    continue;
                }

                var routeKey = (
                    stationFlow.StationSystemId,
                    capabilityResult.Value,
                    lineControllerAuthorization?.ControllerBindingId);
                if (!commandsByRoute.TryGetValue(routeKey, out var commands))
                {
                    commands = new HashSet<string>(StringComparer.Ordinal);
                    commandsByRoute.Add(routeKey, commands);
                }

                commands.Add(action.CommandName);
            }
        }

        if (commandsByRoute.Count == 0)
        {
            return Result.Success<IReadOnlyCollection<ProjectReleasePackageDependencyLock>>([]);
        }

        var resolvedRoutes = new List<(DriverBindingDetails Binding, string[] CommandNames)>();
        foreach (var route in commandsByRoute
                     .OrderBy(item => item.Key.StationSystemId, StringComparer.Ordinal)
                     .ThenBy(item => item.Key.CapabilityId, StringComparer.Ordinal)
                     .ThenBy(item => item.Key.BindingId, StringComparer.Ordinal))
        {
            var bindings = topology.DriverBindings
                .Where(binding => string.Equals(
                    binding.CapabilityId,
                    route.Key.CapabilityId,
                    StringComparison.Ordinal)
                    && (route.Key.BindingId is null || string.Equals(
                        binding.BindingId,
                        route.Key.BindingId,
                        StringComparison.Ordinal))
                    && IsBindingOwnedByStation(
                        binding,
                        route.Key.StationSystemId,
                        topology))
                .Take(2)
                .ToArray();
            if (bindings.Length != 1)
            {
                return Result.Failure<IReadOnlyCollection<ProjectReleasePackageDependencyLock>>(
                    ApplicationError.Conflict(
                        "Projects.ReleaseFlowIrRouteBindingInvalid",
                        $"Flow IR capability {route.Key.CapabilityId} at Station {route.Key.StationSystemId} must resolve to exactly one topology driver binding."));
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
                binding.OwnerSystemId,
                FindOwningStationSystemId(topology, binding.OwnerSystemId),
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

    private sealed record PackageFlowRouteScope(
        string StationSystemId,
        FlowIrDocument Flow,
        IReadOnlyCollection<ProjectReleaseLineControllerAuthorization> LineControllerAuthorizations);

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
            FlowIrTargetReferenceKind.ProductionUnit => topology.Slots.Any(slot =>
                string.Equals(slot.SlotId, reference, StringComparison.Ordinal)
                && string.Equals(slot.MaterialKind, "ProductionUnit", StringComparison.Ordinal)),
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
