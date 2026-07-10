using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.ProjectWorkspaces;

namespace OpenLineOps.Projects.Application.Releases;

public sealed class ProjectReleasePublisher : IProjectReleasePublisher
{
    private readonly IAutomationProjectService _projectService;
    private readonly IAutomationProjectWorkspaceService _workspaceService;
    private readonly IProjectApplicationWorkspaceScopeResolver _scopeResolver;
    private readonly IProjectReleaseSourceResolver _sourceResolver;
    private readonly IProjectReleaseArtifactStore _artifactStore;
    private readonly IClock _clock;

    public ProjectReleasePublisher(
        IAutomationProjectService projectService,
        IAutomationProjectWorkspaceService workspaceService,
        IProjectApplicationWorkspaceScopeResolver scopeResolver,
        IProjectReleaseSourceResolver sourceResolver,
        IProjectReleaseArtifactStore artifactStore,
        IClock clock)
    {
        _projectService = projectService;
        _workspaceService = workspaceService;
        _scopeResolver = scopeResolver;
        _sourceResolver = sourceResolver;
        _artifactStore = artifactStore;
        _clock = clock;
    }

    public async Task<Result<AutomationProjectDetails>> PublishAsync(
        string projectId,
        PublishProjectReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = Validate(projectId, request);
        if (validation is not null)
        {
            return Result.Failure<AutomationProjectDetails>(validation);
        }

        var projectResult = await _projectService
            .GetByIdAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        if (projectResult.IsFailure)
        {
            return Result.Failure<AutomationProjectDetails>(projectResult.Error);
        }

        var project = projectResult.Value;
        var application = project.Applications.SingleOrDefault(candidate => string.Equals(
            candidate.ApplicationId,
            request.ApplicationId,
            StringComparison.Ordinal));
        if (application is null)
        {
            return Result.Failure<AutomationProjectDetails>(ApplicationError.NotFound(
                "Projects.ProjectApplicationNotFound",
                $"Application {request.ApplicationId} was not found in project {projectId}."));
        }

        if (project.Snapshots.Any(snapshot => string.Equals(
                snapshot.SnapshotId,
                request.SnapshotId,
                StringComparison.Ordinal)))
        {
            return Result.Failure<AutomationProjectDetails>(ApplicationError.Conflict(
                "Projects.SnapshotAlreadyExists",
                $"Project snapshot {request.SnapshotId} already exists in project {projectId}."));
        }

        if (string.IsNullOrWhiteSpace(application.TopologyId))
        {
            return Result.Failure<AutomationProjectDetails>(ApplicationError.Conflict(
                "Projects.TopologyNotLinked",
                $"Application {application.ApplicationId} does not have a linked topology."));
        }

        if (!application.ProcessDefinitionIds.Contains(
                request.ProcessDefinitionId,
                StringComparer.Ordinal))
        {
            return Result.Failure<AutomationProjectDetails>(ApplicationError.Conflict(
                "Projects.ProcessNotLinked",
                $"Process definition {request.ProcessDefinitionId} is not linked to application {application.ApplicationId}."));
        }

        // Synchronize the Application project file before freezing its source
        // tree so the release contains the same topology/process entry links
        // that were validated from the aggregate.
        var sourceManifestResult = await _workspaceService
            .SaveManifestAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        if (sourceManifestResult.IsFailure)
        {
            return Result.Failure<AutomationProjectDetails>(sourceManifestResult.Error);
        }

        var scope = await _scopeResolver
            .ResolveAsync(projectId, application.ApplicationId, cancellationToken)
            .ConfigureAwait(false);
        if (scope is null)
        {
            return Result.Failure<AutomationProjectDetails>(ApplicationError.NotFound(
                "Projects.ProjectApplicationNotFound",
                $"Application {application.ApplicationId} was not found in project {projectId}."));
        }

        var sourceResult = await _sourceResolver
            .ResolveAsync(
                scope,
                application.TopologyId,
                request.ProcessDefinitionId,
                request.ConfigurationSnapshotId,
                cancellationToken)
            .ConfigureAwait(false);
        if (sourceResult.IsFailure)
        {
            return Result.Failure<AutomationProjectDetails>(sourceResult.Error);
        }

        var metadata = sourceResult.Value;
        var sourceValidation = ValidateResolvedSource(application.TopologyId, request, metadata);
        if (sourceValidation is not null)
        {
            return Result.Failure<AutomationProjectDetails>(sourceValidation);
        }

        ProjectReleaseArtifactDescriptor release;
        try
        {
            release = await _artifactStore
                .PublishAsync(scope, request.SnapshotId, _clock.UtcNow, metadata, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsArtifactStorageException(exception))
        {
            return Result.Failure<AutomationProjectDetails>(ApplicationError.Conflict(
                "Projects.ReleaseArtifactPublicationFailed",
                exception.Message));
        }

        var descriptorValidation = ValidateReleaseDescriptor(scope, request.SnapshotId, release);
        if (descriptorValidation is not null)
        {
            return Result.Failure<AutomationProjectDetails>(descriptorValidation);
        }

        ProjectApplicationWorkspaceScope releaseScope;
        try
        {
            releaseScope = new ProjectApplicationWorkspaceScope(
                scope.ProjectId,
                scope.ApplicationId,
                release.SourceRootPath,
                release.ApplicationProjectRelativePath);
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<AutomationProjectDetails>(ApplicationError.Conflict(
                "Projects.ReleaseArtifactDescriptorInvalid",
                exception.Message));
        }

        var copiedSourceResult = await _sourceResolver
            .ResolveAsync(
                releaseScope,
                metadata.TopologyId,
                metadata.ProcessDefinitionId,
                metadata.ConfigurationSnapshotId,
                cancellationToken)
            .ConfigureAwait(false);
        if (copiedSourceResult.IsFailure)
        {
            return Result.Failure<AutomationProjectDetails>(ApplicationError.Conflict(
                "Projects.ReleaseArtifactSourceInvalid",
                $"Copied release source could not be resolved: {copiedSourceResult.Error.Message}"));
        }

        if (!SemanticMetadataEquals(metadata, copiedSourceResult.Value))
        {
            return Result.Failure<AutomationProjectDetails>(ApplicationError.Conflict(
                "Projects.ReleaseArtifactSourceMismatch",
                "Copied release source metadata differs from the source resolved before publication."));
        }

        var relativeManifestPathResult = GetProjectRelativeManifestPath(scope, release.ManifestPath);
        if (relativeManifestPathResult.IsFailure)
        {
            return Result.Failure<AutomationProjectDetails>(relativeManifestPathResult.Error);
        }

        var publishResult = await _projectService
            .PublishSnapshotAsync(
                projectId,
                new PublishProjectSnapshotRequest(
                    request.SnapshotId,
                    application.ApplicationId,
                    metadata.TopologyId,
                    metadata.LayoutIds,
                    metadata.ProcessDefinitionId,
                    metadata.ProcessVersionId,
                    metadata.ConfigurationSnapshotId,
                    metadata.CapabilityBindings.Select(binding => new SnapshotCapabilityBindingRequest(
                        binding.CapabilityId,
                        binding.BindingId,
                        binding.ProviderKind,
                        binding.ProviderKey)).ToArray(),
                    metadata.TargetReferences.Select(target => new ProjectTargetReferenceRequest(
                        target.Kind,
                        target.TargetId)).ToArray(),
                    metadata.BlockVersionIds,
                    relativeManifestPathResult.Value,
                    release.ContentSha256),
                cancellationToken)
            .ConfigureAwait(false);
        if (publishResult.IsFailure)
        {
            return Result.Failure<AutomationProjectDetails>(publishResult.Error);
        }

        var manifestResult = await _workspaceService
            .SaveManifestAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        if (manifestResult.IsSuccess)
        {
            return Result.Success(manifestResult.Value.Project);
        }

        var restoreResult = await _workspaceService
            .OpenAsync(
                new OpenAutomationProjectWorkspaceRequest(scope.ProjectPath),
                cancellationToken)
            .ConfigureAwait(false);
        if (restoreResult.IsFailure)
        {
            return Result.Failure<AutomationProjectDetails>(ApplicationError.Conflict(
                "Projects.ProjectManifestRollbackFailed",
                $"Project manifest save failed ({manifestResult.Error.Message}) and the prior manifest could not be restored ({restoreResult.Error.Message})."));
        }

        return Result.Failure<AutomationProjectDetails>(manifestResult.Error);
    }

    private static ApplicationError? Validate(
        string projectId,
        PublishProjectReleaseRequest request)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return Required("Projects.ProjectIdRequired", "ProjectId");
        }

        if (string.IsNullOrWhiteSpace(request.SnapshotId))
        {
            return Required("Projects.SnapshotIdRequired", "SnapshotId");
        }

        if (string.IsNullOrWhiteSpace(request.ApplicationId))
        {
            return Required("Projects.ApplicationIdRequired", "ApplicationId");
        }

        if (string.IsNullOrWhiteSpace(request.ProcessDefinitionId))
        {
            return Required("Projects.ProcessDefinitionIdRequired", "ProcessDefinitionId");
        }

        return string.IsNullOrWhiteSpace(request.ConfigurationSnapshotId)
            ? Required("Projects.ConfigurationSnapshotIdRequired", "ConfigurationSnapshotId")
            : null;
    }

    private static ApplicationError? ValidateResolvedSource(
        string topologyId,
        PublishProjectReleaseRequest request,
        ProjectReleaseSourceMetadata metadata)
    {
        if (!string.Equals(metadata.TopologyId, topologyId, StringComparison.Ordinal)
            || !string.Equals(metadata.ProcessDefinitionId, request.ProcessDefinitionId, StringComparison.Ordinal)
            || !string.Equals(metadata.ConfigurationSnapshotId, request.ConfigurationSnapshotId, StringComparison.Ordinal))
        {
            return ApplicationError.Conflict(
                "Projects.ReleaseSourceIdentityMismatch",
                "Resolved release source identity does not match the requested project application resources.");
        }

        if (metadata.LayoutIds is null || metadata.LayoutIds.Count == 0)
        {
            return ApplicationError.Conflict(
                "Projects.ReleaseLayoutsMissing",
                "Resolved release source does not contain a site layout.");
        }

        if (metadata.CapabilityBindings is null || metadata.CapabilityBindings.Count == 0)
        {
            return ApplicationError.Conflict(
                "Projects.ReleaseCapabilityBindingsMissing",
                "Resolved release source does not contain a capability binding.");
        }

        if (metadata.TargetReferences is null || metadata.TargetReferences.Count == 0)
        {
            return ApplicationError.Conflict(
                "Projects.ReleaseTargetsMissing",
                "Resolved release source does not contain a runtime target.");
        }

        if (string.IsNullOrWhiteSpace(metadata.FlowIrSchemaVersion)
            || string.IsNullOrWhiteSpace(metadata.FlowIrCanonicalJson)
            || !IsSha256(metadata.FlowIrSha256))
        {
            return ApplicationError.Conflict(
                "Projects.ReleaseFlowIrMissing",
                "Resolved release source does not contain a valid frozen Flow IR schema, canonical JSON, and SHA-256.");
        }

        return metadata.BlockVersionIds is null
            ? ApplicationError.Conflict(
                "Projects.ReleaseBlockVersionsMissing",
                "Resolved release source does not contain a block version collection.")
            : null;
    }

    private static ApplicationError? ValidateReleaseDescriptor(
        ProjectApplicationWorkspaceScope scope,
        string snapshotId,
        ProjectReleaseArtifactDescriptor release)
    {
        if (!string.Equals(release.SnapshotId, snapshotId, StringComparison.Ordinal)
            || !string.Equals(release.ProjectId, scope.ProjectId, StringComparison.Ordinal)
            || !string.Equals(release.ApplicationId, scope.ApplicationId, StringComparison.Ordinal))
        {
            return ApplicationError.Conflict(
                "Projects.ReleaseArtifactIdentityMismatch",
                "Published release artifact identity does not match its project snapshot request.");
        }

        if (string.IsNullOrWhiteSpace(release.ManifestPath)
            || string.IsNullOrWhiteSpace(release.ContentSha256)
            || string.IsNullOrWhiteSpace(release.SourceRootPath))
        {
            return ApplicationError.Conflict(
                "Projects.ReleaseArtifactDescriptorInvalid",
                "Published release artifact descriptor is missing its source root, manifest path, or content SHA-256.");
        }

        return null;
    }

    private static bool SemanticMetadataEquals(
        ProjectReleaseSourceMetadata left,
        ProjectReleaseSourceMetadata right)
    {
        if (!string.Equals(left.TopologyId, right.TopologyId, StringComparison.Ordinal)
            || !string.Equals(left.ProcessDefinitionId, right.ProcessDefinitionId, StringComparison.Ordinal)
            || !string.Equals(left.ProcessVersionId, right.ProcessVersionId, StringComparison.Ordinal)
            || !string.Equals(left.FlowIrSchemaVersion, right.FlowIrSchemaVersion, StringComparison.Ordinal)
            || !string.Equals(left.FlowIrSha256, right.FlowIrSha256, StringComparison.Ordinal)
            || !string.Equals(left.FlowIrCanonicalJson, right.FlowIrCanonicalJson, StringComparison.Ordinal)
            || !string.Equals(
                left.ConfigurationSnapshotId,
                right.ConfigurationSnapshotId,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (!SequenceEqualOrdinal(left.LayoutIds, right.LayoutIds)
            || !SequenceEqualOrdinal(left.BlockVersionIds, right.BlockVersionIds))
        {
            return false;
        }

        var leftBindings = left.CapabilityBindings
            .OrderBy(binding => binding.CapabilityId, StringComparer.Ordinal)
            .ThenBy(binding => binding.BindingId, StringComparer.Ordinal)
            .ThenBy(binding => binding.ProviderKind, StringComparer.Ordinal)
            .ThenBy(binding => binding.ProviderKey, StringComparer.Ordinal);
        var rightBindings = right.CapabilityBindings
            .OrderBy(binding => binding.CapabilityId, StringComparer.Ordinal)
            .ThenBy(binding => binding.BindingId, StringComparer.Ordinal)
            .ThenBy(binding => binding.ProviderKind, StringComparer.Ordinal)
            .ThenBy(binding => binding.ProviderKey, StringComparer.Ordinal);
        if (!leftBindings.SequenceEqual(rightBindings))
        {
            return false;
        }

        var leftTargets = left.TargetReferences
            .OrderBy(target => target.Kind, StringComparer.Ordinal)
            .ThenBy(target => target.TargetId, StringComparer.Ordinal);
        var rightTargets = right.TargetReferences
            .OrderBy(target => target.Kind, StringComparer.Ordinal)
            .ThenBy(target => target.TargetId, StringComparer.Ordinal);

        return leftTargets.SequenceEqual(rightTargets);
    }

    private static bool IsSha256(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && value.Length == 64
               && string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal)
               && value.All(Uri.IsHexDigit);
    }

    private static bool SequenceEqualOrdinal(
        IEnumerable<string> left,
        IEnumerable<string> right)
    {
        return left
            .Order(StringComparer.Ordinal)
            .SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static Result<string> GetProjectRelativeManifestPath(
        ProjectApplicationWorkspaceScope scope,
        string manifestPath)
    {
        try
        {
            var projectRoot = Path.GetFullPath(scope.ProjectPath);
            var fullManifestPath = Path.GetFullPath(manifestPath);
            var relativePath = Path.GetRelativePath(projectRoot, fullManifestPath);
            if (Path.IsPathRooted(relativePath)
                || string.Equals(relativePath, "..", StringComparison.Ordinal)
                || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                return Result.Failure<string>(ApplicationError.Conflict(
                    "Projects.ReleaseArtifactPathOutsideProject",
                    $"Release manifest path '{manifestPath}' is outside project root '{scope.ProjectPath}'."));
            }

            return Result.Success(relativePath.Replace('\\', '/'));
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            return Result.Failure<string>(ApplicationError.Conflict(
                "Projects.ReleaseArtifactDescriptorInvalid",
                exception.Message));
        }
    }

    private static bool IsArtifactStorageException(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException;
    }

    private static ApplicationError Required(string code, string fieldName)
    {
        return ApplicationError.Validation(code, $"{fieldName} is required.");
    }
}
