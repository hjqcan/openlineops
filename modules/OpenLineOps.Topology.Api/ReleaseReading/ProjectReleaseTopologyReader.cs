using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Topology.Application.Layouts;
using OpenLineOps.Topology.Application.Persistence;
using OpenLineOps.Topology.Application.Topologies;
using OpenLineOps.Topology.Domain.Identifiers;

namespace OpenLineOps.Topology.Api.ReleaseReading;

public sealed class ProjectReleaseTopologyReader
{
    private readonly IAutomationProjectService _projectService;
    private readonly IProjectApplicationWorkspaceScopeResolver _scopeResolver;
    private readonly IProjectReleaseArtifactStore _releaseStore;
    private readonly IProjectAutomationTopologyRepository _topologyRepository;
    private readonly IProjectSiteLayoutRepository _layoutRepository;

    public ProjectReleaseTopologyReader(
        IAutomationProjectService projectService,
        IProjectApplicationWorkspaceScopeResolver scopeResolver,
        IProjectReleaseArtifactStore releaseStore,
        IProjectAutomationTopologyRepository topologyRepository,
        IProjectSiteLayoutRepository layoutRepository)
    {
        _projectService = projectService;
        _scopeResolver = scopeResolver;
        _releaseStore = releaseStore;
        _topologyRepository = topologyRepository;
        _layoutRepository = layoutRepository;
    }

    public async Task<Result<AutomationTopologyDetails>> GetTopologyAsync(
        string projectId,
        string applicationId,
        string snapshotId,
        string topologyId,
        CancellationToken cancellationToken = default)
    {
        var releaseResult = await OpenReleaseAsync(
                projectId,
                applicationId,
                snapshotId,
                cancellationToken)
            .ConfigureAwait(false);
        if (releaseResult.IsFailure)
        {
            return Result.Failure<AutomationTopologyDetails>(releaseResult.Error);
        }

        var release = releaseResult.Value;
        if (!string.Equals(release.Snapshot.TopologyId, topologyId, StringComparison.Ordinal)
            || !string.Equals(release.Artifact.Metadata.TopologyId, topologyId, StringComparison.Ordinal))
        {
            return Result.Failure<AutomationTopologyDetails>(ApplicationError.Conflict(
                "Topology.ProjectReleaseTopologyMismatch",
                $"Topology {topologyId} is not the frozen topology of project snapshot {snapshotId}."));
        }

        try
        {
            var topology = await _topologyRepository
                .GetByIdAsync(
                    release.Scope,
                    new AutomationTopologyId(topologyId),
                    cancellationToken)
                .ConfigureAwait(false);
            return topology is null
                ? Result.Failure<AutomationTopologyDetails>(ApplicationError.NotFound(
                    "Topology.ProjectReleaseTopologyNotFound",
                    $"Immutable project snapshot {snapshotId} does not contain topology {topologyId}."))
                : Result.Success(AutomationTopologyMapper.ToDetails(topology));
        }
        catch (Exception exception) when (IsInvalidReleaseException(exception))
        {
            return InvalidRelease<AutomationTopologyDetails>(snapshotId, exception);
        }
    }

    public async Task<Result<SiteLayoutDetails>> GetLayoutAsync(
        string projectId,
        string applicationId,
        string snapshotId,
        string layoutId,
        CancellationToken cancellationToken = default)
    {
        var releaseResult = await OpenReleaseAsync(
                projectId,
                applicationId,
                snapshotId,
                cancellationToken)
            .ConfigureAwait(false);
        if (releaseResult.IsFailure)
        {
            return Result.Failure<SiteLayoutDetails>(releaseResult.Error);
        }

        var release = releaseResult.Value;
        if (!release.Snapshot.LayoutIds.Contains(layoutId, StringComparer.Ordinal)
            || !release.Artifact.Metadata.LayoutIds.Contains(layoutId, StringComparer.Ordinal))
        {
            return Result.Failure<SiteLayoutDetails>(ApplicationError.Conflict(
                "Topology.ProjectReleaseLayoutMismatch",
                $"Layout {layoutId} is not frozen in project snapshot {snapshotId}."));
        }

        try
        {
            var layout = await _layoutRepository
                .GetByIdAsync(release.Scope, new SiteLayoutId(layoutId), cancellationToken)
                .ConfigureAwait(false);
            if (layout is null)
            {
                return Result.Failure<SiteLayoutDetails>(ApplicationError.NotFound(
                    "Topology.ProjectReleaseLayoutNotFound",
                    $"Immutable project snapshot {snapshotId} does not contain layout {layoutId}."));
            }

            if (!string.Equals(
                    layout.TopologyId.Value,
                    release.Snapshot.TopologyId,
                    StringComparison.Ordinal))
            {
                return Result.Failure<SiteLayoutDetails>(ApplicationError.Conflict(
                    "Topology.ProjectReleaseLayoutTopologyMismatch",
                    $"Layout {layoutId} does not belong to the frozen topology of project snapshot {snapshotId}."));
            }

            return Result.Success(AutomationTopologyMapper.ToDetails(layout));
        }
        catch (Exception exception) when (IsInvalidReleaseException(exception))
        {
            return InvalidRelease<SiteLayoutDetails>(snapshotId, exception);
        }
    }

    private async Task<Result<ReleaseReadScope>> OpenReleaseAsync(
        string projectId,
        string applicationId,
        string snapshotId,
        CancellationToken cancellationToken)
    {
        if (!IsCanonical(projectId) || !IsCanonical(applicationId) || !IsCanonical(snapshotId))
        {
            return Result.Failure<ReleaseReadScope>(ApplicationError.Validation(
                "Topology.ProjectReleaseScopeInvalid",
                "Project, Application, and snapshot identities must be non-empty canonical values."));
        }

        var projectResult = await _projectService
            .GetByIdAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        if (projectResult.IsFailure)
        {
            return Result.Failure<ReleaseReadScope>(projectResult.Error);
        }

        var project = projectResult.Value;
        var application = project.Applications.SingleOrDefault(candidate => string.Equals(
            candidate.ApplicationId,
            applicationId,
            StringComparison.Ordinal));
        if (application is null)
        {
            return Result.Failure<ReleaseReadScope>(ApplicationError.NotFound(
                "Topology.ProjectApplicationNotFound",
                $"Application {applicationId} was not found in project {projectId}."));
        }

        var snapshot = project.Snapshots.SingleOrDefault(candidate => string.Equals(
            candidate.SnapshotId,
            snapshotId,
            StringComparison.Ordinal));
        if (snapshot is null
            || !string.Equals(snapshot.ApplicationId, applicationId, StringComparison.Ordinal))
        {
            return Result.Failure<ReleaseReadScope>(ApplicationError.NotFound(
                "Topology.ProjectSnapshotNotFound",
                $"Project snapshot {snapshotId} was not found for Application {applicationId}."));
        }

        try
        {
            var liveScope = await _scopeResolver
                .ResolveAsync(projectId, applicationId, cancellationToken)
                .ConfigureAwait(false);
            if (liveScope is null)
            {
                return Result.Failure<ReleaseReadScope>(ApplicationError.NotFound(
                    "Topology.ProjectApplicationNotFound",
                    $"Application {applicationId} was not found in project {projectId}."));
            }

            var artifact = await _releaseStore
                .OpenAsync(liveScope, snapshotId, snapshot.ReleaseContentSha256, cancellationToken)
                .ConfigureAwait(false);
            if (artifact is null)
            {
                return Result.Failure<ReleaseReadScope>(ApplicationError.NotFound(
                    "Topology.ProjectReleaseNotFound",
                    $"Immutable release for project snapshot {snapshotId} was not found."));
            }

            if (!string.Equals(artifact.ProjectId, projectId, StringComparison.Ordinal)
                || !string.Equals(artifact.ApplicationId, applicationId, StringComparison.Ordinal)
                || !string.Equals(artifact.SnapshotId, snapshotId, StringComparison.Ordinal)
                || !string.Equals(
                    artifact.Metadata.ProductionLine.TopologyId,
                    snapshot.TopologyId,
                    StringComparison.Ordinal))
            {
                return Result.Failure<ReleaseReadScope>(ApplicationError.Conflict(
                    "Topology.ProjectReleaseIdentityMismatch",
                    $"Immutable release {snapshotId} does not match its Project, Application, or topology identity."));
            }

            var releaseScope = new ProjectApplicationWorkspaceScope(
                projectId,
                applicationId,
                artifact.SourceRootPath,
                artifact.ApplicationProjectRelativePath);
            return Result.Success(new ReleaseReadScope(snapshot, artifact, releaseScope));
        }
        catch (Exception exception) when (IsInvalidReleaseException(exception))
        {
            return InvalidRelease<ReleaseReadScope>(snapshotId, exception);
        }
    }

    private static bool IsCanonical(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool IsInvalidReleaseException(Exception exception) =>
        exception is ArgumentException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException;

    private static Result<T> InvalidRelease<T>(string snapshotId, Exception exception) =>
        Result.Failure<T>(ApplicationError.Conflict(
            "Topology.ProjectReleaseInvalid",
            $"Immutable release for project snapshot {snapshotId} is invalid: {exception.Message}"));

    private sealed record ReleaseReadScope(
        PublishedProjectSnapshotDetails Snapshot,
        OpenedProjectReleaseArtifact Artifact,
        ProjectApplicationWorkspaceScope Scope);
}
