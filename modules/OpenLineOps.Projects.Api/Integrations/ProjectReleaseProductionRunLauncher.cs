using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Processes.Application.Runtime;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Projects.Api.Integrations;

public sealed class ProjectReleaseProductionRunLauncher : IProjectReleaseProductionRunLauncher
{
    private readonly IProjectApplicationWorkspaceScopeResolver _scopeResolver;
    private readonly IProjectReleaseArtifactStore _releaseStore;
    private readonly IProjectRuntimeConfigurationSnapshotResolver _configurationResolver;
    private readonly IProjectExecutionCoordinator _executionCoordinator;
    private readonly IProductionRunRunner _productionRunRunner;
    private readonly IFlowIrCanonicalSerializer _flowIrSerializer;
    private readonly IFlowIrExecutableRuntimeProcessMapper _flowIrMapper;

    public ProjectReleaseProductionRunLauncher(
        IProjectApplicationWorkspaceScopeResolver scopeResolver,
        IProjectReleaseArtifactStore releaseStore,
        IProjectRuntimeConfigurationSnapshotResolver configurationResolver,
        IProjectExecutionCoordinator executionCoordinator,
        IProductionRunRunner productionRunRunner,
        IFlowIrCanonicalSerializer flowIrSerializer,
        IFlowIrExecutableRuntimeProcessMapper flowIrMapper)
    {
        _scopeResolver = scopeResolver;
        _releaseStore = releaseStore;
        _configurationResolver = configurationResolver;
        _executionCoordinator = executionCoordinator;
        _productionRunRunner = productionRunRunner;
        _flowIrSerializer = flowIrSerializer;
        _flowIrMapper = flowIrMapper;
    }

    public async ValueTask<Result<ProductionRunRunResult>> StartAsync(
        PublishedProjectSnapshotDetails snapshot,
        StartProjectReleaseProductionRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);

        var requestValidation = ValidateRequest(request);
        if (requestValidation is not null)
        {
            return Result.Failure<ProductionRunRunResult>(requestValidation);
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

            await using var executionLease = await _executionCoordinator
                .TryAcquireAsync(liveScope.ProjectPath, cancellationToken)
                .ConfigureAwait(false);
            if (executionLease is null)
            {
                return Failure(
                    "Projects.ProjectExecutionAlreadyActive",
                    $"Project {snapshot.ProjectId} already has an active execution owner.");
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
            var orderedStages = line.Stages.OrderBy(stage => stage.Sequence).ToArray();
            if (orderedStages.Length == 0
                || orderedStages.Select(stage => stage.StageId).Distinct(StringComparer.Ordinal).Count()
                    != orderedStages.Length
                || orderedStages.Where((stage, index) => stage.Sequence != index + 1).Any())
            {
                return Failure(
                    "Projects.ProjectReleaseProductionStagesInvalid",
                    $"Immutable release {snapshot.SnapshotId} does not contain a contiguous, uniquely identified Production stage route.");
            }

            var plans = new List<ProductionStageExecutionPlan>(orderedStages.Length);
            foreach (var stage in orderedStages)
            {
                var workstationMatches = line.Workstations
                    .Where(workstation => string.Equals(
                        workstation.WorkstationId,
                        stage.WorkstationId,
                        StringComparison.Ordinal))
                    .Take(2)
                    .ToArray();
                if (workstationMatches.Length != 1)
                {
                    return Failure(
                        "Projects.ProjectReleaseWorkstationInvalid",
                        $"Production stage {stage.StageId} must reference exactly one frozen Workstation.");
                }

                var flowResult = ResolveFrozenFlowIr(snapshot.SnapshotId, stage);
                if (flowResult.IsFailure)
                {
                    return Result.Failure<ProductionRunRunResult>(flowResult.Error);
                }

                var executableResult = _flowIrMapper.Map(flowResult.Value);
                if (executableResult.IsFailure)
                {
                    return Failure(
                        "Projects.ProjectReleaseFlowIrMappingFailed",
                        $"Production stage {stage.StageId} frozen Flow IR cannot be mapped to Runtime: {executableResult.Error.Message}");
                }

                var configurationResult = await _configurationResolver
                    .ResolveAsync(releaseScope, stage.ConfigurationSnapshotId, cancellationToken)
                    .ConfigureAwait(false);
                if (configurationResult.IsFailure)
                {
                    return Failure(
                        "Projects.ProjectReleaseStageConfigurationInvalid",
                        $"Production stage {stage.StageId} cannot resolve frozen configuration {stage.ConfigurationSnapshotId}: {configurationResult.Error.Message}");
                }

                var configuration = configurationResult.Value;
                var configurationMismatch = FindStageConfigurationMismatch(
                    stage,
                    workstationMatches[0],
                    configuration);
                if (configurationMismatch is not null)
                {
                    return Failure(
                        "Projects.ProjectReleaseStageConfigurationMismatch",
                        $"Production stage {stage.StageId} configuration does not match its frozen route: {configurationMismatch}.");
                }

                plans.Add(new ProductionStageExecutionPlan(
                    line.LineDefinitionId,
                    stage.StageId,
                    stage.Sequence,
                    stage.WorkstationId,
                    new StationId(workstationMatches[0].StationSystemId),
                    new ConfigurationSnapshotId(configuration.ConfigurationSnapshotId),
                    new RecipeSnapshotId(configuration.RecipeSnapshotId),
                    executableResult.Value));
            }

            return await _productionRunRunner
                .RunAsync(
                    new StartProductionRunRequest(
                        new ProductionRunId(request.ProductionRunId),
                        snapshot.ProjectId,
                        snapshot.ApplicationId,
                        snapshot.SnapshotId,
                        snapshot.TopologyId,
                        line.LineDefinitionId,
                        new DutIdentity(
                            line.DutModel.DutModelId,
                            line.DutModel.IdentityInputKey,
                            request.DutIdentityValue),
                        request.ActorId,
                        plans,
                        request.BatchId,
                        request.FixtureId,
                        request.DeviceId),
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
        ProjectReleaseProductionStage stage)
    {
        var documentResult = _flowIrSerializer.Deserialize(stage.FlowIrCanonicalJson);
        if (documentResult.IsFailure)
        {
            return Result.Failure<FlowIrDocument>(ApplicationError.Conflict(
                "Projects.ProjectReleaseFlowIrInvalid",
                $"Production stage {stage.StageId} in release {snapshotId} has invalid canonical Flow IR: {documentResult.Error.Message}"));
        }

        var artifactResult = _flowIrSerializer.Serialize(documentResult.Value);
        if (artifactResult.IsFailure)
        {
            return Result.Failure<FlowIrDocument>(ApplicationError.Conflict(
                "Projects.ProjectReleaseFlowIrInvalid",
                $"Production stage {stage.StageId} in release {snapshotId} Flow IR cannot be serialized canonically: {artifactResult.Error.Message}"));
        }

        var document = documentResult.Value;
        var artifact = artifactResult.Value;
        var blockVersionIds = document.BlockDependencies
            .Select(dependency => dependency.LockId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!string.Equals(stage.FlowIrSchemaVersion, artifact.SchemaVersion, StringComparison.Ordinal)
            || !string.Equals(stage.FlowIrSha256, artifact.Sha256, StringComparison.Ordinal)
            || !string.Equals(stage.FlowIrCanonicalJson, artifact.CanonicalJson, StringComparison.Ordinal)
            || !string.Equals(document.ProcessDefinitionId, stage.FlowDefinitionId, StringComparison.Ordinal)
            || !string.Equals(document.ProcessVersionId, stage.FlowVersionId, StringComparison.Ordinal)
            || !blockVersionIds.SequenceEqual(
                stage.BlockVersionIds.Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            return Result.Failure<FlowIrDocument>(ApplicationError.Conflict(
                "Projects.ProjectReleaseFlowIrIdentityMismatch",
                $"Production stage {stage.StageId} in release {snapshotId} Flow IR identity, dependencies, canonical JSON, or SHA-256 does not match its frozen metadata."));
        }

        return Result.Success(document);
    }

    private static string? FindStageConfigurationMismatch(
        ProjectReleaseProductionStage stage,
        ProjectReleaseWorkstation workstation,
        RuntimeConfigurationSnapshotDetails configuration)
    {
        if (!string.Equals(
                configuration.ConfigurationSnapshotId,
                stage.ConfigurationSnapshotId,
                StringComparison.Ordinal))
        {
            return $"configuration id is {configuration.ConfigurationSnapshotId}, expected {stage.ConfigurationSnapshotId}";
        }

        if (!string.Equals(
                configuration.ProcessDefinitionId,
                stage.FlowDefinitionId,
                StringComparison.Ordinal))
        {
            return $"process definition is {configuration.ProcessDefinitionId}, expected {stage.FlowDefinitionId}";
        }

        if (!string.Equals(
                configuration.ProcessVersionId,
                stage.FlowVersionId,
                StringComparison.Ordinal))
        {
            return $"process version is {configuration.ProcessVersionId}, expected {stage.FlowVersionId}";
        }

        return string.Equals(
            configuration.StationSystemId,
            workstation.StationSystemId,
            StringComparison.Ordinal)
            ? null
            : $"Station system is {configuration.StationSystemId}, expected {workstation.StationSystemId}";
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

    private static ApplicationError? ValidateRequest(StartProjectReleaseProductionRunRequest request)
    {
        if (request.ProductionRunId == Guid.Empty)
        {
            return ApplicationError.Validation(
                "Projects.ProductionRunIdRequired",
                "ProductionRunId must be a non-empty GUID.");
        }

        if (!IsCanonical(request.DutIdentityValue) || !IsCanonical(request.ActorId))
        {
            return ApplicationError.Validation(
                "Projects.ProductionRunIdentityInvalid",
                "DUT identity and actor identity must be non-empty canonical values.");
        }

        return IsCanonicalOptional(request.BatchId)
            && IsCanonicalOptional(request.FixtureId)
            && IsCanonicalOptional(request.DeviceId)
            ? null
            : ApplicationError.Validation(
                "Projects.ProductionRunMetadataInvalid",
                "Optional Production run metadata must be null or non-empty canonical values.");
    }

    private static bool IsCanonical(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool IsCanonicalOptional(string? value) => value is null || IsCanonical(value);

    private static bool SequenceEqualOrdinal(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Order(StringComparer.Ordinal)
            .SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static Result<ProductionRunRunResult> Failure(
        string code,
        string message,
        bool notFound = false) =>
        Result.Failure<ProductionRunRunResult>(notFound
            ? ApplicationError.NotFound(code, message)
            : ApplicationError.Conflict(code, message));
}
