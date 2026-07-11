using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Projects.Application.Persistence;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Projects.Domain.Applications;
using OpenLineOps.Projects.Domain.Identifiers;
using OpenLineOps.Projects.Domain.Projects;
using OpenLineOps.Projects.Domain.Snapshots;

namespace OpenLineOps.StationRuntime;

internal sealed class ImmutableStationProjectRepositoryAdapter : IAutomationProjectRepository
{
    private readonly AutomationProject _project;

    public ImmutableStationProjectRepositoryAdapter(OpenedProjectReleaseArtifact release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var metadata = release.Metadata;
        var projectId = new AutomationProjectId(release.ProjectId);
        var applicationId = new ProjectApplicationId(release.ApplicationId);
        var snapshotId = new PublishedProjectSnapshotId(release.SnapshotId);
        var topologyId = new AutomationTopologyId(metadata.TopologyId);
        var application = ProjectApplication.Restore(
            applicationId,
            release.ApplicationId,
            topologyId,
            metadata.ProductionLine.Operations
                .Select(operation => new ProcessDefinitionId(operation.FlowDefinitionId))
                .Distinct(),
            release.ApplicationProjectRelativePath);
        var snapshot = PublishedProjectSnapshot.Restore(
            snapshotId,
            projectId,
            applicationId,
            topologyId,
            metadata.LayoutIds,
            new ProductionLineDefinitionId(metadata.ProductionLine.LineDefinitionId),
            metadata.CapabilityBindings.Select(binding => new SnapshotCapabilityBinding(
                binding.CapabilityId,
                binding.BindingId,
                binding.ProviderKind,
                binding.ProviderKey,
                binding.OwnerSystemId,
                binding.OwnerStationSystemId)),
            metadata.TargetReferences.Select(target => new ProjectTargetReference(
                target.Kind,
                target.TargetId)),
            metadata.BlockVersionIds,
            ProjectReleaseManifest.FileName,
            release.ContentSha256,
            release.PublishedAtUtc);
        _project = AutomationProject.Restore(
            projectId,
            release.ProjectId,
            release.SourceRootPath,
            release.PublishedAtUtc,
            snapshotId,
            [application],
            [snapshot]);
    }

    public ValueTask SaveAsync(
        AutomationProject project,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new InvalidOperationException(
            "A Station Runtime Project release is immutable."));

    public ValueTask<AutomationProject?> GetByIdAsync(
        AutomationProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<AutomationProject?>(
            projectId == _project.Id ? _project : null);
    }

    public ValueTask<IReadOnlyCollection<AutomationProject>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyCollection<AutomationProject>>([_project]);
    }
}

internal sealed class ImmutableStationReleaseArtifactStoreAdapter(
    OpenedProjectReleaseArtifact release) : IProjectReleaseArtifactStore
{
    public ValueTask<ProjectReleaseArtifactDescriptor> PublishAsync(
        ProjectApplicationWorkspaceScope scope,
        string snapshotId,
        DateTimeOffset publishedAtUtc,
        ProjectReleaseSourceMetadata metadata,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ProjectReleaseArtifactDescriptor>(new InvalidOperationException(
            "A Station Runtime Project release is immutable."));

    public ValueTask<OpenedProjectReleaseArtifact?> OpenAsync(
        ProjectApplicationWorkspaceScope scope,
        string snapshotId,
        string expectedContentSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        var matches = string.Equals(scope.ProjectId, release.ProjectId, StringComparison.Ordinal)
            && string.Equals(scope.ApplicationId, release.ApplicationId, StringComparison.Ordinal)
            && string.Equals(snapshotId, release.SnapshotId, StringComparison.Ordinal)
            && string.Equals(expectedContentSha256, release.ContentSha256, StringComparison.Ordinal);
        return ValueTask.FromResult<OpenedProjectReleaseArtifact?>(matches ? release : null);
    }

    public ValueTask RollbackPublicationAsync(
        ProjectApplicationWorkspaceScope scope,
        string snapshotId,
        string expectedContentSha256,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new InvalidOperationException(
            "A Station Runtime Project release is immutable."));
}

internal sealed class ImmutableStationWorkspaceScopeResolver(
    OpenedProjectReleaseArtifact release) : IProjectApplicationWorkspaceScopeResolver
{
    public ValueTask<ProjectApplicationWorkspaceScope?> ResolveAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ProjectApplicationWorkspaceScope?>(
            string.Equals(projectId, release.ProjectId, StringComparison.Ordinal)
            && string.Equals(applicationId, release.ApplicationId, StringComparison.Ordinal)
                ? new ProjectApplicationWorkspaceScope(
                    release.ProjectId,
                    release.ApplicationId,
                    release.SourceRootPath,
                    release.ApplicationProjectRelativePath)
                : null);
    }
}
