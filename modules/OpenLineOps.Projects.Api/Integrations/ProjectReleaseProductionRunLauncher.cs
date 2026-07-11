using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Processes.Application.Runtime;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Projects.Api.Integrations;

public sealed class ProjectReleaseProductionRunLauncher : IProjectReleaseProductionRunLauncher
{
    private readonly IProjectApplicationWorkspaceScopeResolver _scopeResolver;
    private readonly IProjectReleaseArtifactStore _releaseStore;
    private readonly IProjectRuntimeConfigurationSnapshotResolver _configurationResolver;
    private readonly IProductionRunCoordinator _productionRunCoordinator;
    private readonly IFlowIrCanonicalSerializer _flowIrSerializer;
    private readonly IFlowIrExecutableRuntimeProcessMapper _flowIrMapper;

    public ProjectReleaseProductionRunLauncher(
        IProjectApplicationWorkspaceScopeResolver scopeResolver,
        IProjectReleaseArtifactStore releaseStore,
        IProjectRuntimeConfigurationSnapshotResolver configurationResolver,
        IProductionRunCoordinator productionRunCoordinator,
        IFlowIrCanonicalSerializer flowIrSerializer,
        IFlowIrExecutableRuntimeProcessMapper flowIrMapper)
    {
        _scopeResolver = scopeResolver;
        _releaseStore = releaseStore;
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
            var liveScope = await _scopeResolver
                .ResolveAsync(snapshot.ProjectId, snapshot.ApplicationId, cancellationToken)
                .ConfigureAwait(false);
            if (liveScope is null)
            {
                return Failure(
                    "Projects.ProjectApplicationNotFound",
                    $"Application {snapshot.ApplicationId} was not found in project {snapshot.ProjectId}.",
                    notFound: true);
            }

            var release = await _releaseStore
                .OpenAsync(
                    liveScope,
                    snapshot.SnapshotId,
                    snapshot.ReleaseContentSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            if (release is null)
            {
                return Failure(
                    "Projects.ProjectReleaseNotFound",
                    $"Immutable release for project snapshot {snapshot.SnapshotId} was not found.",
                    notFound: true);
            }

            var mismatch = FindMetadataMismatch(liveScope, snapshot, release);
            if (mismatch is not null)
            {
                return Failure(
                    "Projects.ProjectReleaseMetadataMismatch",
                    $"Immutable release for project snapshot {snapshot.SnapshotId} does not match the published snapshot: {mismatch}.");
            }

            var releaseScope = new ProjectApplicationWorkspaceScope(
                snapshot.ProjectId,
                snapshot.ApplicationId,
                release.SourceRootPath,
                release.ApplicationProjectRelativePath);
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
                    CreateResourceRequirements(operation, request)));
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
                        new ProductionUnitIdentity(
                            line.ProductModel.ProductModelId,
                            line.ProductModel.IdentityInputKey,
                            request.ProductionUnitIdentityValue),
                        request.ActorId,
                        line.EntryOperationId,
                        plans,
                        transitions,
                        request.LotId,
                        request.CarrierId),
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
        ProjectReleaseOperation operation,
        SubmitProjectReleaseProductionRunRequest request)
    {
        var resources = new List<ResourceRequirement>
        {
            new(ResourceKind.Station, operation.StationSystemId)
        };
        AddOptional(ResourceKind.Slot, request.SlotId);
        AddOptional(ResourceKind.Fixture, request.FixtureId);
        AddOptional(ResourceKind.Device, request.DeviceId);
        return resources;

        void AddOptional(ResourceKind kind, string? resourceId)
        {
            if (resourceId is not null)
            {
                resources.Add(new ResourceRequirement(kind, resourceId));
            }
        }
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
                        transition.ExpectedOutputValue!)));
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

    private static string? FindMetadataMismatch(
        ProjectApplicationWorkspaceScope liveScope,
        PublishedProjectSnapshotDetails snapshot,
        OpenedProjectReleaseArtifact release)
    {
        if (!string.Equals(release.SnapshotId, snapshot.SnapshotId, StringComparison.Ordinal)
            || !string.Equals(release.ProjectId, snapshot.ProjectId, StringComparison.Ordinal)
            || !string.Equals(release.ApplicationId, snapshot.ApplicationId, StringComparison.Ordinal))
        {
            return "release Project, Application, or snapshot identity differs";
        }

        if (!string.Equals(release.ContentSha256, snapshot.ReleaseContentSha256, StringComparison.Ordinal))
        {
            return "release content SHA-256 differs";
        }

        var manifestPathMismatch = FindManifestPathMismatch(liveScope, snapshot, release);
        if (manifestPathMismatch is not null)
        {
            return manifestPathMismatch;
        }

        var metadata = release.Metadata;
        if (!string.Equals(metadata.TopologyId, snapshot.TopologyId, StringComparison.Ordinal)
            || !string.Equals(
                metadata.ProductionLine.TopologyId,
                snapshot.TopologyId,
                StringComparison.Ordinal))
        {
            return "Production line topology differs";
        }

        if (!string.Equals(
                metadata.ProductionLine.LineDefinitionId,
                snapshot.ProductionLineDefinitionId,
                StringComparison.Ordinal))
        {
            return "Production line identity differs";
        }

        if (!SequenceEqualOrdinal(metadata.LayoutIds, snapshot.LayoutIds)
            || !SequenceEqualOrdinal(metadata.BlockVersionIds, snapshot.BlockVersionIds))
        {
            return "Layout or Blockly block revision identities differ";
        }

        var releaseBindings = metadata.CapabilityBindings
            .OrderBy(binding => binding.CapabilityId, StringComparer.Ordinal)
            .ThenBy(binding => binding.BindingId, StringComparer.Ordinal)
            .ThenBy(binding => binding.ProviderKind, StringComparer.Ordinal)
            .ThenBy(binding => binding.ProviderKey, StringComparer.Ordinal)
            .ToArray();
        var snapshotBindings = snapshot.CapabilityBindings
            .Select(binding => new ProjectReleaseCapabilityBinding(
                binding.CapabilityId,
                binding.BindingId,
                binding.ProviderKind,
                binding.ProviderKey))
            .OrderBy(binding => binding.CapabilityId, StringComparer.Ordinal)
            .ThenBy(binding => binding.BindingId, StringComparer.Ordinal)
            .ThenBy(binding => binding.ProviderKind, StringComparer.Ordinal)
            .ThenBy(binding => binding.ProviderKey, StringComparer.Ordinal)
            .ToArray();
        if (!releaseBindings.SequenceEqual(snapshotBindings))
        {
            return "capability bindings differ";
        }

        var releaseTargets = metadata.TargetReferences
            .OrderBy(target => target.Kind, StringComparer.Ordinal)
            .ThenBy(target => target.TargetId, StringComparer.Ordinal)
            .ToArray();
        var snapshotTargets = snapshot.TargetReferences
            .Select(target => new ProjectReleaseTargetReference(target.Kind, target.TargetId))
            .OrderBy(target => target.Kind, StringComparer.Ordinal)
            .ThenBy(target => target.TargetId, StringComparer.Ordinal)
            .ToArray();
        return releaseTargets.SequenceEqual(snapshotTargets)
            ? null
            : "target references differ";
    }

    private static string? FindManifestPathMismatch(
        ProjectApplicationWorkspaceScope liveScope,
        PublishedProjectSnapshotDetails snapshot,
        OpenedProjectReleaseArtifact release)
    {
        var declaredPath = snapshot.ReleaseManifestPath;
        if (Path.IsPathRooted(declaredPath) || declaredPath.Contains('\\'))
        {
            return "release manifest path is not a canonical project-relative path";
        }

        var projectRoot = Path.GetFullPath(liveScope.ProjectPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var projectPrefix = projectRoot + Path.DirectorySeparatorChar;
        var declaredFullPath = Path.GetFullPath(Path.Combine(
            projectRoot,
            declaredPath.Replace('/', Path.DirectorySeparatorChar)));
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!declaredFullPath.StartsWith(projectPrefix, pathComparison))
        {
            return "release manifest path escapes the project directory";
        }

        var actualManifestPath = Path.GetFullPath(release.ManifestPath);
        if (!actualManifestPath.StartsWith(projectPrefix, pathComparison))
        {
            return "opened release manifest is outside the project directory";
        }

        var actualRelativePath = Path.GetRelativePath(projectRoot, actualManifestPath)
            .Replace('\\', '/');
        return string.Equals(declaredPath, actualRelativePath, StringComparison.Ordinal)
            ? null
            : $"release manifest path is {actualRelativePath}, expected {declaredPath}";
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

        if (!IsCanonical(request.ProductionUnitIdentityValue) || !IsCanonical(request.ActorId))
        {
            return ApplicationError.Validation(
                "Projects.ProductionRunIdentityInvalid",
                "Production Unit identity and actor identity must be non-empty canonical values.");
        }

        return IsCanonicalOptional(request.LotId)
            && IsCanonicalOptional(request.CarrierId)
            && IsCanonicalOptional(request.SlotId)
            && IsCanonicalOptional(request.FixtureId)
            && IsCanonicalOptional(request.DeviceId)
            ? null
            : ApplicationError.Validation(
                "Projects.ProductionRunMetadataInvalid",
                "Optional Production Run metadata must be null or non-empty canonical values.");
    }

    private static bool IsCanonical(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool IsCanonicalOptional(string? value) => value is null || IsCanonical(value);

    private static bool SequenceEqualOrdinal(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Order(StringComparer.Ordinal)
            .SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static Result<ProductionRunSnapshot> Failure(
        string code,
        string message,
        bool notFound = false) =>
        Result.Failure<ProductionRunSnapshot>(notFound
            ? ApplicationError.NotFound(code, message)
            : ApplicationError.Conflict(code, message));
}
