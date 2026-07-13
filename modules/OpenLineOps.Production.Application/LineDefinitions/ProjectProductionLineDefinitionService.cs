using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Application.ProjectWorkspaces;
using OpenLineOps.Processes.Domain.Identifiers;
using OpenLineOps.Production.Application.Persistence;
using OpenLineOps.Production.Domain.Aggregates;
using OpenLineOps.Production.Domain.Identifiers;
using OpenLineOps.Production.Domain.Models;
using OpenLineOps.Topology.Application.Persistence;
using OpenLineOps.Topology.Application.Topologies;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Systems;
using OpenLineOps.Topology.Domain.Topology;

namespace OpenLineOps.Production.Application.LineDefinitions;

public sealed class ProjectProductionLineDefinitionService : IProjectProductionLineDefinitionService
{
    private readonly IProjectApplicationWorkspaceScopeResolver _scopeResolver;
    private readonly IProjectProductionLineDefinitionRepository _repository;
    private readonly IProjectAutomationTopologyRepository _topologyRepository;
    private readonly IProjectProcessDefinitionRepository _processRepository;
    private readonly IProjectProcessBlocklyBlockCatalog _blockCatalog;
    private readonly IProcessFlowIrCompiler _flowIrCompiler;
    private readonly IClock _clock;

    public ProjectProductionLineDefinitionService(
        IProjectApplicationWorkspaceScopeResolver scopeResolver,
        IProjectProductionLineDefinitionRepository repository,
        IProjectAutomationTopologyRepository topologyRepository,
        IProjectProcessDefinitionRepository processRepository,
        IProjectProcessBlocklyBlockCatalog blockCatalog,
        IProcessFlowIrCompiler flowIrCompiler,
        IClock clock)
    {
        _scopeResolver = scopeResolver;
        _repository = repository;
        _topologyRepository = topologyRepository;
        _processRepository = processRepository;
        _blockCatalog = blockCatalog;
        _flowIrCompiler = flowIrCompiler;
        _clock = clock;
    }

    public Task<Result<ProductionLineDefinitionDetails>> CreateAsync(
        string projectId,
        string applicationId,
        SaveProductionLineDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        return SaveAsync(projectId, applicationId, routeId: null, request, cancellationToken);
    }

    public Task<Result<ProductionLineDefinitionDetails>> ReplaceAsync(
        string projectId,
        string applicationId,
        string lineDefinitionId,
        SaveProductionLineDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        return SaveAsync(projectId, applicationId, lineDefinitionId, request, cancellationToken);
    }

    public async Task<Result<ProductionLineDefinitionDetails>> GetByIdAsync(
        string projectId,
        string applicationId,
        string lineDefinitionId,
        CancellationToken cancellationToken = default)
    {
        var scopeResult = await ResolveScopeAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (scopeResult.IsFailure)
        {
            return Result.Failure<ProductionLineDefinitionDetails>(scopeResult.Error);
        }

        if (string.IsNullOrWhiteSpace(lineDefinitionId))
        {
            return Result.Failure<ProductionLineDefinitionDetails>(Required("LineDefinitionId"));
        }

        try
        {
            var definition = await _repository
                .GetByIdAsync(
                    scopeResult.Value,
                    new ProductionLineDefinitionId(lineDefinitionId),
                    cancellationToken)
                .ConfigureAwait(false);
            return definition is null
                ? Result.Failure<ProductionLineDefinitionDetails>(NotFound(lineDefinitionId))
                : Result.Success(ProductionLineDefinitionMapper.ToDetails(definition));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<ProductionLineDefinitionDetails>(InvalidLineDefinition(exception));
        }
        catch (InvalidDataException exception)
        {
            return Result.Failure<ProductionLineDefinitionDetails>(InvalidApplicationResource(exception));
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return Result.Failure<ProductionLineDefinitionDetails>(ApplicationResourceStorageFailed(exception));
        }
    }

    public async Task<Result<IReadOnlyCollection<ProductionLineDefinitionSummary>>> ListAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        var scopeResult = await ResolveScopeAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (scopeResult.IsFailure)
        {
            return Result.Failure<IReadOnlyCollection<ProductionLineDefinitionSummary>>(scopeResult.Error);
        }

        try
        {
            var definitions = await _repository.ListAsync(scopeResult.Value, cancellationToken)
                .ConfigureAwait(false);
            return Result.Success<IReadOnlyCollection<ProductionLineDefinitionSummary>>(definitions
                .OrderBy(definition => definition.Id.Value, StringComparer.Ordinal)
                .Select(ProductionLineDefinitionMapper.ToSummary)
                .ToArray());
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<IReadOnlyCollection<ProductionLineDefinitionSummary>>(
                InvalidLineDefinition(exception));
        }
        catch (InvalidDataException exception)
        {
            return Result.Failure<IReadOnlyCollection<ProductionLineDefinitionSummary>>(
                InvalidApplicationResource(exception));
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return Result.Failure<IReadOnlyCollection<ProductionLineDefinitionSummary>>(
                ApplicationResourceStorageFailed(exception));
        }
    }

    private async Task<Result<ProductionLineDefinitionDetails>> SaveAsync(
        string projectId,
        string applicationId,
        string? routeId,
        SaveProductionLineDefinitionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scopeResult = await ResolveScopeAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (scopeResult.IsFailure)
        {
            return Result.Failure<ProductionLineDefinitionDetails>(scopeResult.Error);
        }

        if (routeId is not null
            && !string.Equals(routeId, request.LineDefinitionId, StringComparison.Ordinal))
        {
            return Result.Failure<ProductionLineDefinitionDetails>(ApplicationError.Validation(
                "Production.LineDefinitionIdMismatch",
                $"Route line definition id {routeId} does not match request id {request.LineDefinitionId}."));
        }

        try
        {
            var scope = scopeResult.Value;
            var definitionId = new ProductionLineDefinitionId(request.LineDefinitionId);
            var existing = await _repository.GetByIdAsync(scope, definitionId, cancellationToken)
                .ConfigureAwait(false);
            if (routeId is null && existing is not null)
            {
                return Result.Failure<ProductionLineDefinitionDetails>(ApplicationError.Conflict(
                    "Production.LineDefinitionAlreadyExists",
                    $"Production line definition {definitionId} already exists."));
            }

            if (routeId is not null && existing is null)
            {
                return Result.Failure<ProductionLineDefinitionDetails>(NotFound(routeId));
            }

            var now = _clock.UtcNow;
            var definition = BuildDefinition(request, existing?.CreatedAtUtc ?? now, now);
            var semanticError = await ValidateReferencesAsync(scope, definition, cancellationToken)
                .ConfigureAwait(false);
            if (semanticError is not null)
            {
                return Result.Failure<ProductionLineDefinitionDetails>(semanticError);
            }

            await _repository.SaveAsync(scope, definition, cancellationToken).ConfigureAwait(false);
            return Result.Success(ProductionLineDefinitionMapper.ToDetails(definition));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<ProductionLineDefinitionDetails>(InvalidLineDefinition(exception));
        }
        catch (InvalidDataException exception)
        {
            return Result.Failure<ProductionLineDefinitionDetails>(InvalidApplicationResource(exception));
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return Result.Failure<ProductionLineDefinitionDetails>(ApplicationResourceStorageFailed(exception));
        }
    }

    private static ProductionLineDefinition BuildDefinition(
        SaveProductionLineDefinitionRequest request,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request.ProductModel);
        ArgumentNullException.ThrowIfNull(request.Operations);
        ArgumentNullException.ThrowIfNull(request.Transitions);
        ArgumentNullException.ThrowIfNull(request.LineControllerAuthorizations);
        EnsureRequestItemsAreComplete(request);

        var productModel = ProductModelDefinition.Create(
            new ProductModelId(request.ProductModel.ProductModelId),
            request.ProductModel.ModelCode,
            request.ProductModel.IdentityInputKey);
        var operations = request.Operations.Select(operation => OperationDefinition.Create(
            new OperationDefinitionId(operation.OperationId),
            operation.DisplayName,
            operation.StationSystemId,
            operation.FlowDefinitionId,
            operation.ConfigurationSnapshotId,
            operation.Resources.Select(resource => new OperationResourceBinding(
                new OperationResourceBindingId(resource.BindingId),
                resource.Kind,
                resource.TopologyTargetId,
                resource.Resolution))));
        var transitions = request.Transitions.Select(transition => RouteTransition.Create(
            new RouteTransitionId(transition.TransitionId),
            new OperationDefinitionId(transition.SourceOperationId),
            transition.TargetOperationId is null
                ? null
                : new OperationDefinitionId(transition.TargetOperationId),
            transition.TerminalDisposition,
            transition.Kind,
            transition.RequiredJudgement,
            transition.MaxTraversals,
            transition.ParallelGroupId,
            transition.OutputKey is null
                ? null
                : new RouteOutputCondition(
                    transition.OutputKey,
                    new OpenLineOps.Runtime.Contracts.ProductionContextValue(
                        transition.ExpectedOutputKind!.Value,
                        transition.ExpectedOutputValue!))));
        var lineControllerAuthorizations = request.LineControllerAuthorizations.Select(authorization =>
            new LineControllerAuthorization(
                new LineControllerAuthorizationId(authorization.AuthorizationId),
                new OperationDefinitionId(authorization.OperationId),
                authorization.ActionId,
                authorization.ControllerSystemId,
                authorization.ControllerBindingId,
                authorization.ControllerCapabilityId,
                authorization.ControllerAction,
                authorization.TargetStationSystemId,
                authorization.TargetSystemId,
                authorization.TargetBindingId,
                authorization.TargetCapabilityId,
                authorization.TargetAction));
        return ProductionLineDefinition.Restore(
            new ProductionLineDefinitionId(request.LineDefinitionId),
            request.DisplayName,
            request.TopologyId,
            productModel,
            new OperationDefinitionId(request.EntryOperationId),
            operations,
            transitions,
            lineControllerAuthorizations,
            createdAtUtc,
            updatedAtUtc);
    }

    private static void EnsureRequestItemsAreComplete(SaveProductionLineDefinitionRequest request)
    {
        if (request.Operations.Any(static operation => operation is null
                || operation.Resources is null
                || operation.Resources.Any(static resource => resource is null))
            || request.Transitions.Any(static transition => transition is null)
            || request.LineControllerAuthorizations.Any(static authorization => authorization is null))
        {
            throw new ArgumentException(
                "Production line semantic collections cannot contain null items.",
                nameof(request));
        }

        foreach (var transition in request.Transitions)
        {
            if ((transition.TargetOperationId is null) == (transition.TerminalDisposition is null))
            {
                throw new ArgumentException(
                    $"Route transition {transition.TransitionId} requires exactly one target Operation or terminal disposition.",
                    nameof(request));
            }

            var hasAllConditionFields = transition.OutputKey is not null
                && transition.ExpectedOutputKind is not null
                && transition.ExpectedOutputValue is not null;
            var hasAnyConditionField = transition.OutputKey is not null
                || transition.ExpectedOutputKind is not null
                || transition.ExpectedOutputValue is not null;
            if (hasAnyConditionField != hasAllConditionFields
                || (transition.Kind == RouteTransitionKind.Condition) != hasAllConditionFields)
            {
                throw new ArgumentException(
                    $"Route transition {transition.TransitionId} has an incomplete or inapplicable output condition.",
                    nameof(request));
            }
        }

    }

    private async ValueTask<ApplicationError?> ValidateReferencesAsync(
        ProjectApplicationWorkspaceScope scope,
        ProductionLineDefinition definition,
        CancellationToken cancellationToken)
    {
        var topology = await _topologyRepository
            .GetByIdAsync(scope, new AutomationTopologyId(definition.TopologyId), cancellationToken)
            .ConfigureAwait(false);
        if (topology is null)
        {
            return Validation("TopologyNotFound", $"Topology {definition.TopologyId} was not found.");
        }

        var topologyDetails = AutomationTopologyMapper.ToDetails(topology);
        foreach (var operation in definition.Operations)
        {
            var resourceFailure = OperationResourceTopologyValidator.Validate(
                operation,
                topologyDetails);
            if (resourceFailure is not null)
            {
                return Validation(
                    resourceFailure.Code,
                    resourceFailure.Message);
            }

            foreach (var authorization in definition.LineControllerAuthorizations.Where(candidate =>
                         candidate.OperationId == operation.Id))
            {
                var authorizationFailure = OperationResourceTopologyValidator
                    .ValidateLineControllerAuthorization(operation, authorization, topologyDetails);
                if (authorizationFailure is not null)
                {
                    return Validation(
                        authorizationFailure.Code,
                        authorizationFailure.Message);
                }
            }
        }

        var blockCatalogResult = await _blockCatalog
            .ListAsync(scope.ProjectId, scope.ApplicationId, cancellationToken)
            .ConfigureAwait(false);
        if (blockCatalogResult.IsFailure)
        {
            return Validation(
                "OperationFlowBlockCatalogUnavailable",
                $"Application Blockly block catalog cannot be loaded: {blockCatalogResult.Error.Message}");
        }

        var compiledFlows = new Dictionary<string, FlowIrDocument>(StringComparer.Ordinal);
        foreach (var operation in definition.Operations)
        {
            var flow = await _processRepository
                .GetByIdAsync(scope, new ProcessDefinitionId(operation.FlowDefinitionId), cancellationToken)
                .ConfigureAwait(false);
            if (flow is null || !flow.IsPublished)
            {
                return Validation(
                    "OperationFlowNotPublished",
                    $"Operation {operation.Id} must reference an existing published flow.");
            }

            if (!compiledFlows.TryGetValue(operation.FlowDefinitionId, out var flowIr))
            {
                var compilation = _flowIrCompiler.Compile(flow, blockCatalogResult.Value);
                if (compilation.IsFailure)
                {
                    return Validation(
                        "OperationFlowCompilationFailed",
                        $"Operation {operation.Id} flow cannot compile to the standard Flow IR: "
                        + $"{compilation.Error.Code}: {compilation.Error.Message}");
                }

                flowIr = compilation.Value.Document;
                compiledFlows.Add(operation.FlowDefinitionId, flowIr);
            }

            var actions = flowIr.Nodes.SelectMany(node => node.Actions).ToArray();
            var operationAuthorizations = definition.LineControllerAuthorizations
                .Where(authorization => authorization.OperationId == operation.Id)
                .ToArray();
            foreach (var authorization in operationAuthorizations)
            {
                var actionMatches = actions.Where(action => string.Equals(
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
                    return Validation(
                        "LineControllerActionMismatch",
                        $"Line Controller authorization {authorization.Id} must bind exactly one DeviceCommand Flow action whose controller capability/action and remote Driver target match the authorization.");
                }
            }

            foreach (var action in actions)
            {
                if (!OperationResourceTopologyValidator.IsTargetAuthorized(
                        action.Target.Kind.ToString(),
                        action.Target.Reference,
                        operation,
                        topologyDetails)
                    && !operationAuthorizations.Any(authorization =>
                        OperationResourceTopologyValidator.IsLineControllerActionAuthorized(
                            action,
                            authorization)))
                {
                    return Validation(
                        "OperationFlowTargetOutsideStation",
                        $"Operation {operation.Id} Flow action target {action.Target.Kind}:{action.Target.Reference} is outside Station {operation.StationSystemId} and has no exact Line Controller authorization.");
                }
            }
        }

        return null;
    }

    private async ValueTask<Result<ProjectApplicationWorkspaceScope>> ResolveScopeAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(applicationId))
        {
            return Result.Failure<ProjectApplicationWorkspaceScope>(ApplicationError.Validation(
                "Production.ProjectApplicationScopeRequired",
                "ProjectId and ApplicationId are required."));
        }

        var scope = await _scopeResolver.ResolveAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        return scope is null
            ? Result.Failure<ProjectApplicationWorkspaceScope>(ApplicationError.NotFound(
                "Production.ProjectApplicationNotFound",
                $"Application {applicationId} was not found in project {projectId}."))
            : Result.Success(scope);
    }

    private static ApplicationError Required(string fieldName) => ApplicationError.Validation(
        $"Production.{fieldName}Required",
        $"{fieldName} is required.");

    private static ApplicationError Validation(string suffix, string message) =>
        ApplicationError.Validation($"Production.{suffix}", message);

    private static ApplicationError InvalidLineDefinition(Exception exception) =>
        Validation("InvalidLineDefinition", exception.Message);

    private static ApplicationError InvalidApplicationResource(Exception exception) =>
        Validation("InvalidApplicationResource", exception.Message);

    private static ApplicationError ApplicationResourceStorageFailed(Exception exception) =>
        Validation("ApplicationResourceStorageFailed", exception.Message);

    private static bool IsStorageException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or OverflowException;

    private static ApplicationError NotFound(string lineDefinitionId) => ApplicationError.NotFound(
        "Production.LineDefinitionNotFound",
        $"Production line definition {lineDefinitionId} was not found.");
}
