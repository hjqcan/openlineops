using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Projects.Application.Releases;

public interface IProjectReleaseStationPackagePublisher
{
    ValueTask ValidateConfigurationAsync(
        ProjectReleaseStationPackagePreflightRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ProjectReleaseStationPackageSet> PublishAsync(
        ProjectReleaseStationPackageRequest request,
        CancellationToken cancellationToken = default);

    ValueTask RollbackAsync(
        ProjectReleaseStationPackageSet packages,
        CancellationToken cancellationToken = default);
}

public sealed record ProjectReleaseStationPackagePreflightRequest(
    ProjectApplicationWorkspaceScope Scope,
    string ProjectSnapshotId);

public sealed record ProjectReleaseStationPackageRequest(
    ProjectReleaseArtifactDescriptor Release,
    ProjectReleaseSourceMetadata Metadata,
    DateTimeOffset PublishedAtUtc);

public sealed record ProjectReleaseStationPackageSet(
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    IReadOnlyCollection<ProjectReleaseStationPackage> Packages);

public sealed record ProjectReleaseStationPackage(
    string StationSystemId,
    string PackageContentSha256,
    string PackagePath,
    string DeploymentCatalogPath);
