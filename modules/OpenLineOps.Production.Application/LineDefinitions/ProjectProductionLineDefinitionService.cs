using System.Text.Json;
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
using OpenLineOps.Topology.Domain.DriverBindings;
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
        ArgumentNullException.ThrowIfNull(request.ExternalTestProgramAdapters);
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
            operation.ConfigurationSnapshotId));
        var transitions = request.Transitions.Select(transition => RouteTransition.Create(
            new RouteTransitionId(transition.TransitionId),
            new OperationDefinitionId(transition.SourceOperationId),
            new OperationDefinitionId(transition.TargetOperationId),
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
        var adapters = request.ExternalTestProgramAdapters.Select(adapter =>
            ExternalTestProgramAdapter.Create(
                new ExternalTestProgramAdapterId(adapter.AdapterId),
                adapter.DisplayName,
                adapter.CapabilityId,
                adapter.CommandName,
                adapter.Executable,
                adapter.ProviderKey,
                adapter.ArgumentTemplates,
                adapter.InputMappings.Select(mapping =>
                    new ExternalTestProgramInputMapping(mapping.Source, mapping.Target)),
                adapter.ResultMappings.Select(mapping =>
                    new ExternalTestProgramResultMapping(mapping.SourcePath, mapping.TargetKey)),
                new ExternalTestProgramOutcomeMapping(
                    adapter.OutcomeMapping.SourcePath,
                    adapter.OutcomeMapping.PassedToken,
                    adapter.OutcomeMapping.FailedToken,
                    adapter.OutcomeMapping.AbortedToken),
                MillisecondsToTimeout(adapter.TimeoutMilliseconds)));
        return ProductionLineDefinition.Restore(
            new ProductionLineDefinitionId(request.LineDefinitionId),
            request.DisplayName,
            request.TopologyId,
            productModel,
            new OperationDefinitionId(request.EntryOperationId),
            operations,
            transitions,
            adapters,
            createdAtUtc,
            updatedAtUtc);
    }

    private static void EnsureRequestItemsAreComplete(SaveProductionLineDefinitionRequest request)
    {
        if (request.Operations.Any(static operation => operation is null)
            || request.Transitions.Any(static transition => transition is null)
            || request.ExternalTestProgramAdapters.Any(static adapter => adapter is null))
        {
            throw new ArgumentException(
                "Production line semantic collections cannot contain null items.",
                nameof(request));
        }

        foreach (var transition in request.Transitions)
        {
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

        foreach (var adapter in request.ExternalTestProgramAdapters)
        {
            ArgumentNullException.ThrowIfNull(adapter.ArgumentTemplates);
            ArgumentNullException.ThrowIfNull(adapter.InputMappings);
            ArgumentNullException.ThrowIfNull(adapter.ResultMappings);
            ArgumentNullException.ThrowIfNull(adapter.OutcomeMapping);
            if (adapter.InputMappings.Any(static mapping => mapping is null)
                || adapter.ResultMappings.Any(static mapping => mapping is null))
            {
                throw new ArgumentException(
                    $"External test adapter {adapter.AdapterId} mappings cannot contain null items.",
                    nameof(request));
            }
        }
    }

    private static TimeSpan MillisecondsToTimeout(long timeoutMilliseconds)
    {
        var maximumMilliseconds = TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond;
        if (timeoutMilliseconds <= 0 || timeoutMilliseconds > maximumMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeoutMilliseconds),
                "External test program timeout must be a positive whole number of milliseconds representable by TimeSpan.");
        }

        return TimeSpan.FromTicks(checked(timeoutMilliseconds * TimeSpan.TicksPerMillisecond));
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

        foreach (var operation in definition.Operations)
        {
            var stationSystem = topology.Systems.SingleOrDefault(system =>
                system.Id.Value == operation.StationSystemId);
            if (stationSystem is not StationSystem)
            {
                return Validation(
                    "OperationStationSystemInvalid",
                    $"Operation {operation.Id} must reference an existing Station system.");
            }
        }

        foreach (var adapter in definition.ExternalTestProgramAdapters)
        {
            var capability = topology.Capabilities.SingleOrDefault(candidate =>
                candidate.Id.Value == adapter.CapabilityId);
            if (capability is null
                || !string.Equals(capability.CommandName, adapter.CommandName, StringComparison.Ordinal)
                || capability.Timeout != adapter.Timeout)
            {
                return Validation(
                    "ExternalTestCapabilityInvalid",
                    $"External test adapter {adapter.Id} must match one topology capability command and timeout.");
            }

            var binding = topology.DriverBindings.SingleOrDefault(candidate =>
                candidate.CapabilityId.Value == adapter.CapabilityId);
            if (binding is null || !DriverBindingMatches(adapter, binding))
            {
                return Validation(
                    "ExternalTestProviderInvalid",
                    $"External test adapter {adapter.Id} does not match its topology driver binding.");
            }

            if (adapter.Executable is not null)
            {
                var executablePath = ResolveApplicationFile(scope, adapter.Executable);
                if (!File.Exists(executablePath))
                {
                    return Validation(
                        "ExternalTestExecutableNotFound",
                        $"External test executable '{adapter.Executable}' was not found in the Application.");
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
        var usedAdapterIds = new HashSet<ExternalTestProgramAdapterId>();
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

            var stationSystem = topology.Systems.Single(system =>
                system.Id.Value == operation.StationSystemId);
            foreach (var action in flowIr.Nodes.SelectMany(node => node.Actions))
            {
                if (!IsWithinOperationStation(action.Target, operation, topology))
                {
                    return Validation(
                        "OperationFlowTargetOutsideStation",
                        $"Operation {operation.Id} Flow action target {action.Target.Kind}:{action.Target.Reference} is outside Station {operation.StationSystemId}.");
                }

                var adapterReference = ReadExternalAdapterReference(action);
                if (adapterReference.IsMalformed)
                {
                    return Validation(
                        "ExternalTestActionResourceInvalid",
                        $"Operation {operation.Id} contains an invalid external test program resource reference.");
                }

                if (adapterReference.AdapterId is null)
                {
                    continue;
                }

                ExternalTestProgramAdapterId adapterId;
                try
                {
                    adapterId = new ExternalTestProgramAdapterId(adapterReference.AdapterId);
                }
                catch (ArgumentException)
                {
                    return Validation(
                        "ExternalTestActionResourceInvalid",
                        $"Operation {operation.Id} contains a non-canonical external test program resource id.");
                }

                var adapter = definition.ExternalTestProgramAdapters.SingleOrDefault(candidate =>
                    candidate.Id == adapterId);
                if (adapter is null)
                {
                    return Validation(
                        "ExternalTestActionResourceNotFound",
                        $"Operation {operation.Id} references missing external test program resource {adapterId}.");
                }

                if (stationSystem.ProvidedCapabilities.All(capability =>
                        capability.Value != adapter.CapabilityId))
                {
                    return Validation(
                        "ExternalTestCapabilityNotProvidedByStation",
                        $"Operation {operation.Id} Station system does not provide capability {adapter.CapabilityId}.");
                }

                if (!IsExternalTestAction(action, adapter, operation))
                {
                    return Validation(
                        "ExternalTestActionContractMismatch",
                        $"Operation {operation.Id} external test action does not match resource {adapter.Id} and its Station target.");
                }

                usedAdapterIds.Add(adapter.Id);
            }
        }

        var unusedAdapter = definition.ExternalTestProgramAdapters.FirstOrDefault(adapter =>
            !usedAdapterIds.Contains(adapter.Id));
        if (unusedAdapter is not null)
        {
            return Validation(
                "ExternalTestActionMissing",
                $"External test program resource {unusedAdapter.Id} must be referenced by at least one operation Flow action.");
        }

        return null;
    }

    private static bool DriverBindingMatches(
        ExternalTestProgramAdapter adapter,
        OpenLineOps.Topology.Domain.DriverBindings.DriverBinding binding)
    {
        if (adapter.ProviderKey is not null)
        {
            return binding.ProviderKind is DriverProviderKind.ExternalSystem
                    or DriverProviderKind.ProcessCommandProvider
                    or DriverProviderKind.PluginCommand
                && string.Equals(binding.ProviderKey, adapter.ProviderKey, StringComparison.Ordinal);
        }

        return binding.ProviderKind == DriverProviderKind.ExternalSystem
            && string.Equals(binding.ProviderKey, adapter.Id.Value, StringComparison.Ordinal);
    }

    private static bool IsExternalTestAction(
        FlowIrAction action,
        ExternalTestProgramAdapter adapter,
        OperationDefinition operation)
    {
        var expectedTimeoutMilliseconds = checked(
            adapter.Timeout.Ticks / TimeSpan.TicksPerMillisecond);
        if (action.Kind != FlowIrActionKind.DeviceCommand
            || !string.Equals(action.RequiredCapability, adapter.CapabilityId, StringComparison.Ordinal)
            || !string.Equals(action.CommandName, adapter.CommandName, StringComparison.Ordinal)
            || action.Execution.TimeoutMilliseconds != expectedTimeoutMilliseconds
            || !IsOperationStationTarget(action.Target, operation)
            || string.IsNullOrWhiteSpace(action.InputPayload))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(action.InputPayload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var adapterIdProperties = document.RootElement
                .EnumerateObject()
                .Where(property => string.Equals(
                    property.Name,
                    ExternalTestProgramAdapter.InvocationPayloadAdapterIdProperty,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            return adapterIdProperties.Length == 1
                && adapterIdProperties[0].Value.ValueKind == JsonValueKind.String
                && string.Equals(
                    adapterIdProperties[0].Value.GetString(),
                    adapter.Id.Value,
                    StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static (string? AdapterId, bool IsMalformed) ReadExternalAdapterReference(
        FlowIrAction action)
    {
        if (string.IsNullOrWhiteSpace(action.InputPayload))
        {
            return (null, false);
        }

        try
        {
            using var document = JsonDocument.Parse(action.InputPayload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, false);
            }

            var properties = document.RootElement
                .EnumerateObject()
                .Where(property => string.Equals(
                    property.Name,
                    ExternalTestProgramAdapter.InvocationPayloadAdapterIdProperty,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (properties.Length == 0)
            {
                return (null, false);
            }

            return properties.Length == 1 && properties[0].Value.ValueKind == JsonValueKind.String
                ? (properties[0].Value.GetString(), false)
                : (null, true);
        }
        catch (JsonException)
        {
            return (null, false);
        }
    }

    private static bool IsOperationStationTarget(
        FlowIrTargetReference target,
        OperationDefinition operation)
    {
        return target.Kind == FlowIrTargetReferenceKind.System
            && string.Equals(
                target.Reference,
                operation.StationSystemId,
                StringComparison.Ordinal);
    }

    private static bool IsWithinOperationStation(
        FlowIrTargetReference target,
        OperationDefinition operation,
        AutomationTopology topology)
    {
        return target.Kind switch
        {
            FlowIrTargetReferenceKind.System => topology.Systems.Any(system =>
                string.Equals(system.Id.Value, target.Reference, StringComparison.Ordinal)
                && (string.Equals(
                        system.Id.Value,
                        operation.StationSystemId,
                        StringComparison.Ordinal)
                    || string.Equals(
                        system.ParentSystemId?.Value,
                        operation.StationSystemId,
                        StringComparison.Ordinal))),
            FlowIrTargetReferenceKind.SlotGroup => topology.SlotGroups.Any(group =>
                string.Equals(group.Id.Value, target.Reference, StringComparison.Ordinal)
                && string.Equals(
                    group.ParentSystemId.Value,
                    operation.StationSystemId,
                    StringComparison.Ordinal)),
            FlowIrTargetReferenceKind.Slot => topology.Slots.Any(slot =>
                string.Equals(slot.Id.Value, target.Reference, StringComparison.Ordinal)
                && string.Equals(
                    slot.ParentSystemId.Value,
                    operation.StationSystemId,
                    StringComparison.Ordinal)),
            FlowIrTargetReferenceKind.ProductionUnit => true,
            FlowIrTargetReferenceKind.Capability => topology.Systems.Any(system =>
                IsStationOrDirectChild(system, operation.StationSystemId)
                && system.ProvidedCapabilities.Any(capability => string.Equals(
                    capability.Value,
                    target.Reference,
                    StringComparison.Ordinal))),
            FlowIrTargetReferenceKind.Driver => topology.DriverBindings.Any(binding =>
                string.Equals(binding.Id.Value, target.Reference, StringComparison.Ordinal)
                && topology.Systems.Any(system =>
                    IsStationOrDirectChild(system, operation.StationSystemId)
                    && system.ProvidedCapabilities.Contains(binding.CapabilityId))),
            _ => false
        };
    }

    private static bool IsStationOrDirectChild(
        OpenLineOps.Topology.Domain.Systems.AutomationSystem system,
        string stationSystemId) =>
        string.Equals(system.Id.Value, stationSystemId, StringComparison.Ordinal)
        || string.Equals(system.ParentSystemId?.Value, stationSystemId, StringComparison.Ordinal);

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
            throw new InvalidDataException($"Application path '{relativePath}' escapes the Application root.");
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
