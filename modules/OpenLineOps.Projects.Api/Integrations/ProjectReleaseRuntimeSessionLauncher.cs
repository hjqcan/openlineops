using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Application.Runtime;
using OpenLineOps.Processes.Domain.Identifiers;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Runtime.Application.Sessions;
using OpenLineOps.Runtime.Domain.Sessions;
using RuntimeConfigurationSnapshotId = OpenLineOps.Runtime.Domain.Identifiers.ConfigurationSnapshotId;
using RuntimeRecipeSnapshotId = OpenLineOps.Runtime.Domain.Identifiers.RecipeSnapshotId;
using RuntimeStationId = OpenLineOps.Runtime.Domain.Identifiers.StationId;

namespace OpenLineOps.Projects.Api.Integrations;

public sealed class ProjectReleaseRuntimeSessionLauncher : IProjectReleaseRuntimeSessionLauncher
{
    private readonly IProjectApplicationWorkspaceScopeResolver _scopeResolver;
    private readonly IProjectReleaseArtifactStore _releaseStore;
    private readonly IProjectProcessDefinitionRepository _processRepository;
    private readonly IProjectRuntimeConfigurationSnapshotResolver _configurationResolver;
    private readonly IRuntimeSessionRunner _sessionRunner;
    private readonly IFlowIrCanonicalSerializer _flowIrSerializer;
    private readonly IFlowIrExecutableRuntimeProcessMapper _flowIrMapper;

    public ProjectReleaseRuntimeSessionLauncher(
        IProjectApplicationWorkspaceScopeResolver scopeResolver,
        IProjectReleaseArtifactStore releaseStore,
        IProjectProcessDefinitionRepository processRepository,
        IProjectRuntimeConfigurationSnapshotResolver configurationResolver,
        IRuntimeSessionRunner sessionRunner,
        IFlowIrCanonicalSerializer flowIrSerializer,
        IFlowIrExecutableRuntimeProcessMapper flowIrMapper)
    {
        _scopeResolver = scopeResolver;
        _releaseStore = releaseStore;
        _processRepository = processRepository;
        _configurationResolver = configurationResolver;
        _sessionRunner = sessionRunner;
        _flowIrSerializer = flowIrSerializer;
        _flowIrMapper = flowIrMapper;
    }

    public async ValueTask<Result<StartedProcessRuntimeSessionDetails>> StartAsync(
        PublishedProjectSnapshotDetails snapshot,
        StartProcessRuntimeSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            // The live scope is used only to locate the immutable release directory.
            // No live application source is read after the release has been opened.
            var liveScope = await _scopeResolver
                .ResolveAsync(snapshot.ProjectId, snapshot.ApplicationId, cancellationToken)
                .ConfigureAwait(false);
            if (liveScope is null)
            {
                return Result.Failure<StartedProcessRuntimeSessionDetails>(ApplicationError.NotFound(
                    "Projects.ProjectApplicationNotFound",
                    $"Application {snapshot.ApplicationId} was not found in project {snapshot.ProjectId}."));
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
                return Result.Failure<StartedProcessRuntimeSessionDetails>(ApplicationError.NotFound(
                    "Projects.ProjectReleaseNotFound",
                    $"Immutable release for project snapshot {snapshot.SnapshotId} was not found."));
            }

            var metadataMismatch = FindMetadataMismatch(liveScope, snapshot, release);
            if (metadataMismatch is not null)
            {
                return Failure(
                    "Projects.ProjectReleaseMetadataMismatch",
                    $"Immutable release for project snapshot {snapshot.SnapshotId} does not match the published snapshot: {metadataMismatch}.");
            }

            var releaseScope = new ProjectApplicationWorkspaceScope(
                snapshot.ProjectId,
                snapshot.ApplicationId,
                release.SourceRootPath,
                release.ApplicationProjectRelativePath);
            var processDefinitionId = new ProcessDefinitionId(snapshot.ProcessDefinitionId);
            var processDefinition = await _processRepository
                .GetByIdAsync(releaseScope, processDefinitionId, cancellationToken)
                .ConfigureAwait(false);
            if (processDefinition is null)
            {
                return Failure(
                    "Projects.ProjectReleaseProcessNotFound",
                    $"Immutable release {snapshot.SnapshotId} does not contain process definition {snapshot.ProcessDefinitionId}.");
            }

            if (!string.Equals(
                    processDefinition.VersionId.Value,
                    snapshot.ProcessVersionId,
                    StringComparison.Ordinal))
            {
                return Failure(
                    "Projects.ProjectReleaseProcessVersionMismatch",
                    $"Immutable release {snapshot.SnapshotId} contains process version {processDefinition.VersionId}, expected {snapshot.ProcessVersionId}.");
            }

            if (!processDefinition.IsPublished)
            {
                return Failure(
                    "Projects.ProjectReleaseProcessNotPublished",
                    $"Immutable release {snapshot.SnapshotId} contains an unpublished process definition {snapshot.ProcessDefinitionId}.");
            }

            var frozenFlowIrResult = ResolveFrozenFlowIr(snapshot, release.Metadata);
            if (frozenFlowIrResult.IsFailure)
            {
                return Result.Failure<StartedProcessRuntimeSessionDetails>(frozenFlowIrResult.Error);
            }

            var executableProcessResult = _flowIrMapper.Map(frozenFlowIrResult.Value);
            if (executableProcessResult.IsFailure)
            {
                return Failure(
                    "Projects.ProjectReleaseFlowIrMappingFailed",
                    $"Immutable release {snapshot.SnapshotId} Flow IR cannot be mapped to the runtime process: {executableProcessResult.Error.Message}");
            }

            var configurationResult = await _configurationResolver
                .ResolveAsync(releaseScope, snapshot.ConfigurationSnapshotId, cancellationToken)
                .ConfigureAwait(false);
            if (configurationResult.IsFailure)
            {
                return Failure(
                    "Projects.ProjectReleaseConfigurationInvalid",
                    $"Immutable release {snapshot.SnapshotId} cannot resolve configuration snapshot {snapshot.ConfigurationSnapshotId}: {configurationResult.Error.Message}");
            }

            var configuration = configurationResult.Value;
            var configurationMismatch = FindConfigurationMismatch(snapshot, release.Metadata, configuration);
            if (configurationMismatch is not null)
            {
                return Failure(
                    "Projects.ProjectReleaseConfigurationMismatch",
                    $"Immutable release {snapshot.SnapshotId} configuration does not match the published snapshot: {configurationMismatch}.");
            }

            var runResult = await _sessionRunner
                .RunAsync(
                    new OpenLineOps.Runtime.Application.Sessions.StartRuntimeSessionRequest(
                        new RuntimeStationId(release.Metadata.StationSystemId),
                        new RuntimeConfigurationSnapshotId(configuration.ConfigurationSnapshotId),
                        new RuntimeRecipeSnapshotId(configuration.RecipeSnapshotId),
                        executableProcessResult.Value,
                        new RuntimeSessionTraceMetadata(
                            request.SerialNumber,
                            request.BatchId,
                            request.FixtureId,
                            request.DeviceId,
                            request.ActorId,
                            snapshot.ProjectId,
                            snapshot.ApplicationId,
                            snapshot.SnapshotId,
                            snapshot.TopologyId)),
                    cancellationToken)
                .ConfigureAwait(false);

            return runResult.IsFailure
                ? Result.Failure<StartedProcessRuntimeSessionDetails>(runResult.Error)
                : Result.Success(ToDetails(runResult.Value));
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

    private static string? FindMetadataMismatch(
        ProjectApplicationWorkspaceScope liveScope,
        PublishedProjectSnapshotDetails snapshot,
        OpenedProjectReleaseArtifact release)
    {
        if (!string.Equals(release.SnapshotId, snapshot.SnapshotId, StringComparison.Ordinal))
        {
            return $"snapshot id is {release.SnapshotId}, expected {snapshot.SnapshotId}";
        }

        if (!string.Equals(release.ProjectId, snapshot.ProjectId, StringComparison.Ordinal))
        {
            return $"project id is {release.ProjectId}, expected {snapshot.ProjectId}";
        }

        if (!string.Equals(release.ApplicationId, snapshot.ApplicationId, StringComparison.Ordinal))
        {
            return $"application id is {release.ApplicationId}, expected {snapshot.ApplicationId}";
        }

        if (!string.Equals(
                release.ContentSha256,
                snapshot.ReleaseContentSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return "release content SHA-256 differs";
        }

        var manifestPathMismatch = FindManifestPathMismatch(liveScope, snapshot, release);
        if (manifestPathMismatch is not null)
        {
            return manifestPathMismatch;
        }

        var metadata = release.Metadata;
        if (!string.Equals(metadata.TopologyId, snapshot.TopologyId, StringComparison.Ordinal))
        {
            return $"topology id is {metadata.TopologyId}, expected {snapshot.TopologyId}";
        }

        if (string.IsNullOrWhiteSpace(metadata.StationSystemId)
            || !metadata.TargetReferences.Any(target =>
                string.Equals(target.Kind, "System", StringComparison.Ordinal)
                && string.Equals(target.TargetId, metadata.StationSystemId, StringComparison.Ordinal)))
        {
            return "station system id is missing or is not a frozen System target";
        }

        if (!SequenceEqualOrdinal(metadata.LayoutIds, snapshot.LayoutIds))
        {
            return "layout ids differ";
        }

        if (!string.Equals(
                metadata.ProcessDefinitionId,
                snapshot.ProcessDefinitionId,
                StringComparison.Ordinal))
        {
            return $"process definition id is {metadata.ProcessDefinitionId}, expected {snapshot.ProcessDefinitionId}";
        }

        if (!string.Equals(metadata.ProcessVersionId, snapshot.ProcessVersionId, StringComparison.Ordinal))
        {
            return $"process version is {metadata.ProcessVersionId}, expected {snapshot.ProcessVersionId}";
        }

        if (!string.Equals(
                metadata.ConfigurationSnapshotId,
                snapshot.ConfigurationSnapshotId,
                StringComparison.Ordinal))
        {
            return $"configuration snapshot id is {metadata.ConfigurationSnapshotId}, expected {snapshot.ConfigurationSnapshotId}";
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
        if (!releaseTargets.SequenceEqual(snapshotTargets))
        {
            return "target references differ";
        }

        return SequenceEqualOrdinal(metadata.BlockVersionIds, snapshot.BlockVersionIds)
            ? null
            : "block version ids differ";
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
        if (!declaredFullPath.StartsWith(projectPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return "release manifest path escapes the project directory";
        }

        var actualManifestPath = Path.GetFullPath(release.ManifestPath);
        if (!actualManifestPath.StartsWith(projectPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return "opened release manifest is outside the project directory";
        }

        var actualRelativePath = Path.GetRelativePath(projectRoot, actualManifestPath)
            .Replace('\\', '/');
        return string.Equals(declaredPath, actualRelativePath, StringComparison.Ordinal)
            ? null
            : $"release manifest path is {actualRelativePath}, expected {declaredPath}";
    }

    private static string? FindConfigurationMismatch(
        PublishedProjectSnapshotDetails snapshot,
        ProjectReleaseSourceMetadata metadata,
        RuntimeConfigurationSnapshotDetails configuration)
    {
        if (!string.Equals(
                configuration.ConfigurationSnapshotId,
                snapshot.ConfigurationSnapshotId,
                StringComparison.Ordinal))
        {
            return $"configuration snapshot id is {configuration.ConfigurationSnapshotId}, expected {snapshot.ConfigurationSnapshotId}";
        }

        if (!string.Equals(
                configuration.ProcessDefinitionId,
                snapshot.ProcessDefinitionId,
                StringComparison.Ordinal))
        {
            return $"process definition id is {configuration.ProcessDefinitionId}, expected {snapshot.ProcessDefinitionId}";
        }

        if (!string.Equals(
                configuration.StationSystemId,
                metadata.StationSystemId,
                StringComparison.Ordinal))
        {
            return $"station system id is {configuration.StationSystemId}, expected {metadata.StationSystemId}";
        }

        return string.Equals(
            configuration.ProcessVersionId,
            snapshot.ProcessVersionId,
            StringComparison.Ordinal)
            ? null
            : $"process version is {configuration.ProcessVersionId}, expected {snapshot.ProcessVersionId}";
    }

    private Result<FlowIrDocument> ResolveFrozenFlowIr(
        PublishedProjectSnapshotDetails snapshot,
        ProjectReleaseSourceMetadata metadata)
    {
        var frozenDocumentResult = _flowIrSerializer.Deserialize(metadata.FlowIrCanonicalJson);
        if (frozenDocumentResult.IsFailure)
        {
            return Result.Failure<FlowIrDocument>(ApplicationError.Conflict(
                "Projects.ProjectReleaseFlowIrInvalid",
                $"Immutable release {snapshot.SnapshotId} contains invalid canonical Flow IR: {frozenDocumentResult.Error.Message}"));
        }

        var frozenArtifactResult = _flowIrSerializer.Serialize(frozenDocumentResult.Value);
        if (frozenArtifactResult.IsFailure)
        {
            return Result.Failure<FlowIrDocument>(ApplicationError.Conflict(
                "Projects.ProjectReleaseFlowIrInvalid",
                $"Immutable release {snapshot.SnapshotId} Flow IR cannot be serialized canonically: {frozenArtifactResult.Error.Message}"));
        }

        var frozenArtifact = frozenArtifactResult.Value;
        if (!string.Equals(metadata.FlowIrSchemaVersion, frozenArtifact.SchemaVersion, StringComparison.Ordinal)
            || !string.Equals(metadata.FlowIrSha256, frozenArtifact.Sha256, StringComparison.Ordinal)
            || !string.Equals(metadata.FlowIrCanonicalJson, frozenArtifact.CanonicalJson, StringComparison.Ordinal))
        {
            return Result.Failure<FlowIrDocument>(ApplicationError.Conflict(
                "Projects.ProjectReleaseFlowIrInvalid",
                $"Immutable release {snapshot.SnapshotId} Flow IR schema, canonical JSON, and SHA-256 do not agree."));
        }

        var frozenDocument = frozenDocumentResult.Value;
        if (!string.Equals(
                frozenDocument.ProcessDefinitionId,
                snapshot.ProcessDefinitionId,
                StringComparison.Ordinal)
            || !string.Equals(
                frozenDocument.ProcessVersionId,
                snapshot.ProcessVersionId,
                StringComparison.Ordinal))
        {
            return Result.Failure<FlowIrDocument>(ApplicationError.Conflict(
                "Projects.ProjectReleaseFlowIrIdentityMismatch",
                $"Immutable release {snapshot.SnapshotId} Flow IR identity is {frozenDocument.ProcessDefinitionId}/{frozenDocument.ProcessVersionId}, expected {snapshot.ProcessDefinitionId}/{snapshot.ProcessVersionId}."));
        }

        return Result.Success(frozenDocument);
    }

    private static StartedProcessRuntimeSessionDetails ToDetails(RuntimeSessionRunResult runResult)
    {
        return new StartedProcessRuntimeSessionDetails(
            runResult.SessionId.Value,
            runResult.ConfigurationSnapshotId.Value,
            runResult.Status.ToString(),
            runResult.CompletedSteps,
            runResult.CommandCount,
            runResult.IncidentCount);
    }

    private static bool SequenceEqualOrdinal(
        IEnumerable<string> left,
        IEnumerable<string> right)
    {
        return left
            .Order(StringComparer.Ordinal)
            .SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static Result<StartedProcessRuntimeSessionDetails> Failure(string code, string message)
    {
        return Result.Failure<StartedProcessRuntimeSessionDetails>(
            ApplicationError.Conflict(code, message));
    }

}
