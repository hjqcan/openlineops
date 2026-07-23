using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Processes.Application.Runtime;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Projects.Api.Integrations;

public sealed class ProjectReleaseProductionRunLauncher : IProjectReleaseProductionRunLauncher
{
    private readonly IProjectReleaseSnapshotReader _releaseReader;
    private readonly IProjectRuntimeConfigurationSnapshotResolver _configurationResolver;
    private readonly IProductionRunCoordinator _productionRunCoordinator;
    private readonly IFlowIrCanonicalSerializer _flowIrSerializer;
    private readonly IFlowIrExecutableRuntimeProcessMapper _flowIrMapper;

    public ProjectReleaseProductionRunLauncher(
        IProjectReleaseSnapshotReader releaseReader,
        IProjectRuntimeConfigurationSnapshotResolver configurationResolver,
        IProductionRunCoordinator productionRunCoordinator,
        IFlowIrCanonicalSerializer flowIrSerializer,
        IFlowIrExecutableRuntimeProcessMapper flowIrMapper)
    {
        _releaseReader = releaseReader;
        _configurationResolver = configurationResolver;
        _productionRunCoordinator = productionRunCoordinator;
        _flowIrSerializer = flowIrSerializer;
        _flowIrMapper = flowIrMapper;
    }

    public async ValueTask<Result<ProductionRunSnapshot>> SubmitAsync(
        PublishedProjectSnapshotDetails snapshot,
        SubmitProjectReleaseProductionRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);
        var requestValidation = ValidateRequest(request);
        if (requestValidation is not null)
        {
            return Result.Failure<ProductionRunSnapshot>(requestValidation);
        }

        try
        {
            var releaseResult = await _releaseReader
                .OpenAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);
            if (releaseResult.IsFailure)
            {
                return Result.Failure<ProductionRunSnapshot>(releaseResult.Error);
            }

            var release = releaseResult.Value.Artifact;
            var releaseScope = releaseResult.Value.ReleaseScope;
            var line = release.Metadata.ProductionLine;
            var operations = line.Operations.ToArray();
            if (operations.Length == 0
                || operations.Select(operation => operation.OperationId)
                    .Distinct(StringComparer.Ordinal).Count() != operations.Length
                || !operations.Any(operation => string.Equals(
                    operation.OperationId,
                    line.EntryOperationId,
                    StringComparison.Ordinal)))
            {
                return Failure(
                    "Projects.ProjectReleaseOperationsInvalid",
                    $"Immutable release {snapshot.SnapshotId} has invalid or duplicate Operations or no entry Operation.");
            }

            var plans = new List<OperationExecutionPlan>(operations.Length);
            foreach (var operation in operations)
            {
                var flowResult = ResolveFrozenFlowIr(snapshot.SnapshotId, operation);
                if (flowResult.IsFailure)
                {
                    return Result.Failure<ProductionRunSnapshot>(flowResult.Error);
                }

                var executableResult = _flowIrMapper.Map(flowResult.Value);
                if (executableResult.IsFailure)
                {
                    return Failure(
                        "Projects.ProjectReleaseFlowIrMappingFailed",
                        $"Operation {operation.OperationId} frozen Flow IR cannot be mapped to Runtime: {executableResult.Error.Message}");
                }

                var configurationResult = await _configurationResolver
                    .ResolveAsync(
                        releaseScope,
                        operation.ConfigurationSnapshotId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (configurationResult.IsFailure)
                {
                    return Failure(
                        "Projects.ProjectReleaseOperationConfigurationInvalid",
                        $"Operation {operation.OperationId} cannot resolve frozen configuration {operation.ConfigurationSnapshotId}: {configurationResult.Error.Message}");
                }

                var configuration = configurationResult.Value;
                var configurationMismatch = FindOperationConfigurationMismatch(
                    operation,
                    configuration);
                if (configurationMismatch is not null)
                {
                    return Failure(
                        "Projects.ProjectReleaseOperationConfigurationMismatch",
                        $"Operation {operation.OperationId} configuration does not match its frozen route: {configurationMismatch}.");
                }

                plans.Add(new OperationExecutionPlan(
                    operation.OperationId,
                    operation.StationSystemId,
                    new StationId(operation.StationSystemId),
                    new ConfigurationSnapshotId(configuration.ConfigurationSnapshotId),
                    new RecipeSnapshotId(configuration.RecipeSnapshotId),
                    executableResult.Value,
                    operation.InputMappings.Select(mapping => new OperationInputMappingPlan(
                        mapping.TargetInputKey,
                        mapping.SourceOperationId,
                        mapping.SourceOutputKey,
                        ParseExact<ProductionContextValueKind>(
                            mapping.ExpectedValueKind,
                            "Operation input mapping value kind"))),
                    CreateResourceRequirements(
                        line.LineDefinitionId,
                        operation,
                        line.LineControllerAuthorizations),
                    CreateMaterialSlotRequirement(operation)));
            }

            var transitions = line.Transitions
                .Select(ToRuntimeTransition)
                .ToArray();
            return await _productionRunCoordinator
                .SubmitAsync(
                    new SubmitProductionRunRequest(
                        new ProductionRunId(request.ProductionRunId),
                        snapshot.ProjectId,
                        snapshot.ApplicationId,
                        snapshot.SnapshotId,
                        snapshot.TopologyId,
                        line.LineDefinitionId,
                        new ProductionUnitId(request.ProductionUnitId),
                        line.ProductModel.ProductModelId,
                        line.ProductModel.IdentityInputKey,
                        request.ActorId,
                        line.EntryOperationId,
                        plans,
                        transitions),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidDataException
                                           or IOException
                                           or UnauthorizedAccessException)
        {
            return Failure(
                "Projects.ProjectReleaseInvalid",
                $"Immutable release for project snapshot {snapshot.SnapshotId} is invalid: {exception.Message}");
        }
    }

    private Result<FlowIrDocument> ResolveFrozenFlowIr(
        string snapshotId,
        ProjectReleaseOperation operation)
    {
        var documentResult = _flowIrSerializer.Deserialize(operation.FlowIrCanonicalJson);
        if (documentResult.IsFailure)
        {
            return Result.Failure<FlowIrDocument>(ApplicationError.Conflict(
                "Projects.ProjectReleaseFlowIrInvalid",
                $"Operation {operation.OperationId} in release {snapshotId} has invalid canonical Flow IR: {documentResult.Error.Message}"));
        }

        var artifactResult = _flowIrSerializer.Serialize(documentResult.Value);
        if (artifactResult.IsFailure)
        {
            return Result.Failure<FlowIrDocument>(ApplicationError.Conflict(
                "Projects.ProjectReleaseFlowIrInvalid",
                $"Operation {operation.OperationId} in release {snapshotId} Flow IR cannot be serialized canonically: {artifactResult.Error.Message}"));
        }

        var document = documentResult.Value;
        var artifact = artifactResult.Value;
        var blockVersionIds = document.BlockDependencies
            .Select(dependency => dependency.LockId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!string.Equals(operation.FlowIrSchema, artifact.SchemaVersion, StringComparison.Ordinal)
            || !string.Equals(operation.FlowIrSha256, artifact.Sha256, StringComparison.Ordinal)
            || !string.Equals(operation.FlowIrCanonicalJson, artifact.CanonicalJson, StringComparison.Ordinal)
            || !string.Equals(document.ProcessDefinitionId, operation.FlowDefinitionId, StringComparison.Ordinal)
            || !string.Equals(document.ProcessVersionId, operation.FlowVersionId, StringComparison.Ordinal)
            || !blockVersionIds.SequenceEqual(
                operation.BlockVersionIds.Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            return Result.Failure<FlowIrDocument>(ApplicationError.Conflict(
                "Projects.ProjectReleaseFlowIrIdentityMismatch",
                $"Operation {operation.OperationId} in release {snapshotId} Flow IR identity, dependencies, canonical JSON, or SHA-256 does not match frozen metadata."));
        }

        return Result.Success(document);
    }

    private static string? FindOperationConfigurationMismatch(
        ProjectReleaseOperation operation,
        RuntimeConfigurationSnapshotDetails configuration)
    {
        if (!string.Equals(
                configuration.ConfigurationSnapshotId,
                operation.ConfigurationSnapshotId,
                StringComparison.Ordinal))
        {
            return $"configuration id is {configuration.ConfigurationSnapshotId}, expected {operation.ConfigurationSnapshotId}";
        }

        if (!string.Equals(
                configuration.ProcessDefinitionId,
                operation.FlowDefinitionId,
                StringComparison.Ordinal))
        {
            return $"process definition is {configuration.ProcessDefinitionId}, expected {operation.FlowDefinitionId}";
        }

        if (!string.Equals(
                configuration.ProcessVersionId,
                operation.FlowVersionId,
                StringComparison.Ordinal))
        {
            return $"process version is {configuration.ProcessVersionId}, expected {operation.FlowVersionId}";
        }

        return string.Equals(
            configuration.StationSystemId,
            operation.StationSystemId,
            StringComparison.Ordinal)
            ? null
            : $"Station system is {configuration.StationSystemId}, expected {operation.StationSystemId}";
    }

    private static List<ResourceRequirement> CreateResourceRequirements(
        string productionLineDefinitionId,
        ProjectReleaseOperation operation,
        IReadOnlyCollection<ProjectReleaseLineControllerAuthorization> lineControllerAuthorizations)
    {
        if (operation.Resources is null || operation.Resources.Count == 0)
        {
            throw new InvalidDataException(
                $"Operation {operation.OperationId} has no frozen resources.");
        }

        var resources = new List<ResourceRequirement>(
            operation.Resources.Count + (operation.AuthorizedActions.Count * 2));
        foreach (var resource in operation.Resources)
        {
            if (!string.Equals(resource.Resolution, "Fixed", StringComparison.Ordinal))
            {
                if (!string.Equals(resource.Kind, "Slot", StringComparison.Ordinal)
                    || resource.Resolution is not "CurrentMaterialSlot" and not "AvailableSlotInGroup")
                {
                    throw new InvalidDataException(
                        $"Operation {operation.OperationId} resource {resource.BindingId} has invalid dynamic resolution.");
                }

                continue;
            }

            var kind = resource.Kind switch
            {
                "Station" => ResourceKind.Station,
                "Fixture" => ResourceKind.Fixture,
                "Device" => ResourceKind.Device,
                "SlotGroup" => ResourceKind.SlotGroup,
                "Slot" => ResourceKind.Slot,
                _ => throw new InvalidDataException(
                    $"Operation {operation.OperationId} resource {resource.BindingId} has invalid kind '{resource.Kind}'.")
            };
            var resourceId = kind == ResourceKind.Slot
                ? $"{productionLineDefinitionId}/{operation.StationSystemId}/{resource.TopologyTargetId}"
                : resource.TopologyTargetId;
            resources.Add(new ResourceRequirement(kind, resourceId));
        }

        foreach (var authorization in lineControllerAuthorizations.Where(authorization =>
                     string.Equals(
                         authorization.OperationId,
                         operation.OperationId,
                         StringComparison.Ordinal)))
        {
            resources.Add(new ResourceRequirement(
                ResourceKind.Station,
                authorization.TargetStationSystemId));
            resources.Add(new ResourceRequirement(
                ResourceKind.Device,
                authorization.TargetBindingId));
        }

        resources = resources.Distinct().ToList();

        if (resources.Count(resource => resource.Kind == ResourceKind.Station
                && string.Equals(
                    resource.ResourceId,
                    operation.StationSystemId,
                    StringComparison.Ordinal)) != 1
            || resources.Count(resource => resource.Kind == ResourceKind.Station) !=
                1 + lineControllerAuthorizations
                    .Where(authorization => string.Equals(
                        authorization.OperationId,
                        operation.OperationId,
                        StringComparison.Ordinal))
                    .Select(authorization => authorization.TargetStationSystemId)
                    .Distinct(StringComparer.Ordinal)
                    .Count())
        {
            throw new InvalidDataException(
                $"Operation {operation.OperationId} frozen resources do not contain one unique Station lease.");
        }

        return resources;
    }

    private static MaterialSlotRequirement? CreateMaterialSlotRequirement(
        ProjectReleaseOperation operation)
    {
        var dynamicSlots = operation.Resources
            .Where(resource => string.Equals(resource.Kind, "Slot", StringComparison.Ordinal)
                && !string.Equals(resource.Resolution, "Fixed", StringComparison.Ordinal))
            .ToArray();
        if (dynamicSlots.Length > 1)
        {
            throw new InvalidDataException(
                $"Operation {operation.OperationId} declares more than one dynamic material Slot resource.");
        }

        if (dynamicSlots.Length == 0)
        {
            return null;
        }

        var resource = dynamicSlots[0];
        var resolution = resource.Resolution switch
        {
            "CurrentMaterialSlot" => MaterialSlotResolution.CurrentMaterialSlot,
            "AvailableSlotInGroup" => MaterialSlotResolution.AvailableSlotInGroup,
            _ => throw new InvalidDataException(
                $"Operation {operation.OperationId} resource {resource.BindingId} has invalid Slot resolution '{resource.Resolution}'.")
        };
        return new MaterialSlotRequirement(
            resolution,
            resource.TopologyTargetId,
            resource.EligibleSlotIds);
    }

    private static RouteTransitionDefinition ToRuntimeTransition(
        ProjectReleaseRouteTransition transition)
    {
        if (!Enum.TryParse<RuntimeRouteTransitionKind>(
                transition.Kind,
                ignoreCase: false,
                out var kind)
            || !Enum.IsDefined(kind)
            || !string.Equals(kind.ToString(), transition.Kind, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Route transition {transition.TransitionId} has invalid kind '{transition.Kind}'.");
        }

        ResultJudgement? judgement = null;
        if (transition.RequiredJudgement is not null)
        {
            if (!Enum.TryParse<ResultJudgement>(
                    transition.RequiredJudgement,
                    ignoreCase: false,
                    out var parsed)
                || !Enum.IsDefined(parsed)
                || !string.Equals(
                    parsed.ToString(),
                    transition.RequiredJudgement,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Route transition {transition.TransitionId} has invalid judgement '{transition.RequiredJudgement}'.");
            }

            judgement = parsed;
        }

        return new RouteTransitionDefinition(
            transition.TransitionId,
            transition.SourceOperationId,
            transition.TargetOperationId,
            kind,
            judgement,
            transition.MaxTraversals,
            transition.ParallelGroupId,
            transition.OutputKey is null
                ? null
                : new OpenLineOps.Runtime.Domain.Runs.RouteOutputCondition(
                    transition.OutputKey,
                    new ProductionContextValue(
                        ParseExact<ProductionContextValueKind>(
                            transition.ExpectedOutputKind!,
                            "Production Context value kind"),
                        transition.ExpectedOutputValue!)),
            transition.TerminalDisposition is null
                ? null
                : ParseExact<ProductDisposition>(
                    transition.TerminalDisposition,
                    "terminal disposition"));
    }

    private static T ParseExact<T>(string value, string description)
        where T : struct, Enum
    {
        if (!Enum.TryParse<T>(value, ignoreCase: false, out var parsed)
            || !Enum.IsDefined(parsed)
            || !string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Route transition has invalid {description} '{value}'.");
        }

        return parsed;
    }

    private static ApplicationError? ValidateRequest(
        SubmitProjectReleaseProductionRunRequest request)
    {
        if (request.ProductionRunId == Guid.Empty)
        {
            return ApplicationError.Validation(
                "Projects.ProductionRunIdRequired",
                "ProductionRunId must be a non-empty GUID.");
        }

        if (request.ProductionUnitId == Guid.Empty || !IsCanonical(request.ActorId))
        {
            return ApplicationError.Validation(
                "Projects.ProductionRunIdentityInvalid",
                "ProductionUnitId must be a non-empty GUID and actor identity must be canonical.");
        }

        return null;
    }

    private static bool IsCanonical(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static Result<ProductionRunSnapshot> Failure(
        string code,
        string message) =>
        Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(code, message));
}
