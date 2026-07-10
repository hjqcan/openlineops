using System.Text.Json;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Domain.Identifiers;
using OpenLineOps.Production.Application.Persistence;
using OpenLineOps.Production.Domain.Aggregates;
using OpenLineOps.Production.Domain.Identifiers;
using OpenLineOps.Production.Domain.Models;
using OpenLineOps.Topology.Application.Persistence;
using OpenLineOps.Topology.Domain.DriverBindings;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Systems;

namespace OpenLineOps.Production.Application.LineDefinitions;

public sealed class ProjectProductionLineDefinitionService : IProjectProductionLineDefinitionService
{
    private readonly IProjectApplicationWorkspaceScopeResolver _scopeResolver;
    private readonly IProjectProductionLineDefinitionRepository _repository;
    private readonly IProjectAutomationTopologyRepository _topologyRepository;
    private readonly IProjectProcessDefinitionRepository _processRepository;
    private readonly IProcessFlowIrCompiler _flowIrCompiler;
    private readonly IClock _clock;

    public ProjectProductionLineDefinitionService(
        IProjectApplicationWorkspaceScopeResolver scopeResolver,
        IProjectProductionLineDefinitionRepository repository,
        IProjectAutomationTopologyRepository topologyRepository,
        IProjectProcessDefinitionRepository processRepository,
        IProcessFlowIrCompiler flowIrCompiler,
        IClock clock)
    {
        _scopeResolver = scopeResolver;
        _repository = repository;
        _topologyRepository = topologyRepository;
        _processRepository = processRepository;
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
        ArgumentNullException.ThrowIfNull(request.DutModel);
        ArgumentNullException.ThrowIfNull(request.Workstations);
        ArgumentNullException.ThrowIfNull(request.Stages);
        ArgumentNullException.ThrowIfNull(request.ExternalTestProgramAdapters);
        EnsureRequestItemsAreComplete(request);

        var dutModel = DutModelDefinition.Create(
            new DutModelId(request.DutModel.DutModelId),
            request.DutModel.ModelCode,
            request.DutModel.IdentityInputKey);
        var workstations = request.Workstations.Select(workstation => WorkstationDefinition.Create(
            new WorkstationId(workstation.WorkstationId),
            workstation.DisplayName,
            workstation.StationSystemId));
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
                MillisecondsToTimeout(adapter.TimeoutMilliseconds)));
        var stages = request.Stages.Select(stage => ProcessStage.Create(
            new ProcessStageId(stage.StageId),
            stage.Sequence,
            stage.DisplayName,
            new WorkstationId(stage.WorkstationId),
            stage.FlowDefinitionId,
            string.IsNullOrWhiteSpace(stage.ExternalTestProgramAdapterId)
                ? null
                : new ExternalTestProgramAdapterId(stage.ExternalTestProgramAdapterId)));

        return ProductionLineDefinition.Restore(
            new ProductionLineDefinitionId(request.LineDefinitionId),
            request.DisplayName,
            request.TopologyId,
            dutModel,
            workstations,
            stages,
            adapters,
            createdAtUtc,
            updatedAtUtc);
    }

    private static void EnsureRequestItemsAreComplete(SaveProductionLineDefinitionRequest request)
    {
        if (request.Workstations.Any(static workstation => workstation is null)
            || request.Stages.Any(static stage => stage is null)
            || request.ExternalTestProgramAdapters.Any(static adapter => adapter is null))
        {
            throw new ArgumentException(
                "Production line semantic collections cannot contain null items.",
                nameof(request));
        }

        foreach (var adapter in request.ExternalTestProgramAdapters)
        {
            ArgumentNullException.ThrowIfNull(adapter.ArgumentTemplates);
            ArgumentNullException.ThrowIfNull(adapter.InputMappings);
            ArgumentNullException.ThrowIfNull(adapter.ResultMappings);
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

        foreach (var workstation in definition.Workstations)
        {
            var stationSystem = topology.Systems.SingleOrDefault(system =>
                system.Id.Value == workstation.StationSystemId);
            if (stationSystem is not StationSystem)
            {
                return Validation(
                    "WorkstationStationSystemInvalid",
                    $"Workstation {workstation.Id} must reference an existing Station system.");
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

        var compiledFlows = new Dictionary<string, FlowIrDocument>(StringComparer.Ordinal);
        foreach (var stage in definition.Stages)
        {
            var flow = await _processRepository
                .GetByIdAsync(scope, new ProcessDefinitionId(stage.FlowDefinitionId), cancellationToken)
                .ConfigureAwait(false);
            if (flow is null || !flow.IsPublished)
            {
                return Validation(
                    "StageFlowNotPublished",
                    $"Process stage {stage.Id} must reference an existing published flow.");
            }

            if (!compiledFlows.TryGetValue(stage.FlowDefinitionId, out var flowIr))
            {
                var compilation = _flowIrCompiler.Compile(flow);
                if (compilation.IsFailure)
                {
                    return Validation(
                        "StageFlowCompilationFailed",
                        $"Process stage {stage.Id} flow cannot compile to the standard Flow IR: "
                        + $"{compilation.Error.Code}: {compilation.Error.Message}");
                }

                flowIr = compilation.Value.Document;
                compiledFlows.Add(stage.FlowDefinitionId, flowIr);
            }

            if (stage.ExternalTestProgramAdapterId is null)
            {
                continue;
            }

            var adapter = definition.ExternalTestProgramAdapters.Single(candidate =>
                candidate.Id == stage.ExternalTestProgramAdapterId);
            var workstation = definition.Workstations.Single(candidate => candidate.Id == stage.WorkstationId);
            var stationSystem = topology.Systems.Single(candidate =>
                candidate.Id.Value == workstation.StationSystemId);
            if (stationSystem.ProvidedCapabilities.All(capability => capability.Value != adapter.CapabilityId))
            {
                return Validation(
                    "ExternalTestCapabilityNotProvidedByWorkstation",
                    $"Workstation {workstation.Id} Station system does not provide capability {adapter.CapabilityId}.");
            }

            var matchingActions = flowIr.Nodes
                .SelectMany(node => node.Actions)
                .Where(action => IsExternalTestAction(action, adapter, workstation))
                .Take(2)
                .ToArray();
            if (matchingActions.Length != 1)
            {
                return Validation(
                    "ExternalTestActionMissing",
                    $"Process stage {stage.Id} flow must contain exactly one matching external test command action.");
            }
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
        WorkstationDefinition workstation)
    {
        var expectedTimeoutMilliseconds = checked(
            adapter.Timeout.Ticks / TimeSpan.TicksPerMillisecond);
        if (action.Kind != FlowIrActionKind.DeviceCommand
            || !string.Equals(action.RequiredCapability, adapter.CapabilityId, StringComparison.Ordinal)
            || !string.Equals(action.CommandName, adapter.CommandName, StringComparison.Ordinal)
            || action.Execution.TimeoutMilliseconds != expectedTimeoutMilliseconds
            || !IsWorkstationSystemTarget(action.Target, workstation)
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

    private static bool IsWorkstationSystemTarget(
        FlowIrTargetReference target,
        WorkstationDefinition workstation)
    {
        return target.Kind == FlowIrTargetReferenceKind.System
            && string.Equals(
                target.Reference,
                workstation.StationSystemId,
                StringComparison.Ordinal);
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
