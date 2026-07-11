using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Projects.Application.Projects;

namespace OpenLineOps.Projects.Application.Releases;

public sealed class ProjectReleaseSnapshotReader(
    IProjectApplicationWorkspaceScopeResolver scopeResolver,
    IProjectReleaseArtifactStore releaseStore) : IProjectReleaseSnapshotReader
{
    public async ValueTask<Result<VerifiedProjectRelease>> OpenAsync(
        PublishedProjectSnapshotDetails snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        try
        {
            var projectScope = await scopeResolver
                .ResolveAsync(snapshot.ProjectId, snapshot.ApplicationId, cancellationToken)
                .ConfigureAwait(false);
            if (projectScope is null)
            {
                return Failure(
                    ApplicationError.NotFound(
                        "Projects.ProjectApplicationNotFound",
                        $"Application {snapshot.ApplicationId} was not found in project {snapshot.ProjectId}."));
            }

            var release = await releaseStore
                .OpenAsync(
                    projectScope,
                    snapshot.SnapshotId,
                    snapshot.ReleaseContentSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            if (release is null)
            {
                return Failure(ApplicationError.NotFound(
                    "Projects.ProjectReleaseNotFound",
                    $"Immutable release for project snapshot {snapshot.SnapshotId} was not found."));
            }

            var mismatch = FindMetadataMismatch(projectScope, snapshot, release);
            if (mismatch is not null)
            {
                return Failure(ApplicationError.Conflict(
                    "Projects.ProjectReleaseMetadataMismatch",
                    $"Immutable release for project snapshot {snapshot.SnapshotId} does not match the published snapshot: {mismatch}."));
            }

            var releaseScope = new ProjectApplicationWorkspaceScope(
                snapshot.ProjectId,
                snapshot.ApplicationId,
                release.SourceRootPath,
                release.ApplicationProjectRelativePath);
            return Result.Success(new VerifiedProjectRelease(snapshot, releaseScope, release));
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidDataException
                                           or IOException
                                           or UnauthorizedAccessException)
        {
            return Failure(ApplicationError.Conflict(
                "Projects.ProjectReleaseInvalid",
                $"Immutable release for project snapshot {snapshot.SnapshotId} is invalid: {exception.Message}"));
        }
    }

    private static string? FindMetadataMismatch(
        ProjectApplicationWorkspaceScope projectScope,
        PublishedProjectSnapshotDetails snapshot,
        OpenedProjectReleaseArtifact release)
    {
        if (!string.Equals(release.SnapshotId, snapshot.SnapshotId, StringComparison.Ordinal)
            || !string.Equals(release.ProjectId, snapshot.ProjectId, StringComparison.Ordinal)
            || !string.Equals(release.ApplicationId, snapshot.ApplicationId, StringComparison.Ordinal))
        {
            return "release Project, Application, or snapshot identity differs";
        }

        if (!string.Equals(
                release.ApplicationProjectRelativePath,
                projectScope.ApplicationProjectRelativePath,
                StringComparison.Ordinal))
        {
            return "release Application project path differs";
        }

        if (!string.Equals(release.ContentSha256, snapshot.ReleaseContentSha256, StringComparison.Ordinal))
        {
            return "release content SHA-256 differs";
        }

        var manifestPathMismatch = FindManifestPathMismatch(projectScope, snapshot, release);
        if (manifestPathMismatch is not null)
        {
            return manifestPathMismatch;
        }

        var metadata = release.Metadata;
        if (ContainsDuplicateSnapshotIdentity(snapshot)
            || ContainsDuplicateReleaseIdentity(metadata))
        {
            return "published snapshot or release metadata contains duplicate frozen identities";
        }

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
            .OrderBy(binding => binding.OwnerSystemId, StringComparer.Ordinal)
            .ThenBy(binding => binding.CapabilityId, StringComparer.Ordinal)
            .ThenBy(binding => binding.BindingId, StringComparer.Ordinal)
            .ThenBy(binding => binding.ProviderKind, StringComparer.Ordinal)
            .ThenBy(binding => binding.ProviderKey, StringComparer.Ordinal)
            .ThenBy(binding => binding.OwnerStationSystemId, StringComparer.Ordinal)
            .ToArray();
        var snapshotBindings = snapshot.CapabilityBindings
            .Select(binding => new ProjectReleaseCapabilityBinding(
                binding.CapabilityId,
                binding.BindingId,
                binding.ProviderKind,
                binding.ProviderKey,
                binding.OwnerSystemId,
                binding.OwnerStationSystemId))
            .OrderBy(binding => binding.OwnerSystemId, StringComparer.Ordinal)
            .ThenBy(binding => binding.CapabilityId, StringComparer.Ordinal)
            .ThenBy(binding => binding.BindingId, StringComparer.Ordinal)
            .ThenBy(binding => binding.ProviderKind, StringComparer.Ordinal)
            .ThenBy(binding => binding.ProviderKey, StringComparer.Ordinal)
            .ThenBy(binding => binding.OwnerStationSystemId, StringComparer.Ordinal)
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
        ProjectApplicationWorkspaceScope projectScope,
        PublishedProjectSnapshotDetails snapshot,
        OpenedProjectReleaseArtifact release)
    {
        var declaredPath = snapshot.ReleaseManifestPath;
        if (Path.IsPathRooted(declaredPath) || declaredPath.Contains('\\'))
        {
            return "release manifest path is not a canonical project-relative path";
        }

        var projectRoot = Path.GetFullPath(projectScope.ProjectPath)
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

    private static bool SequenceEqualOrdinal(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Order(StringComparer.Ordinal)
            .SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool ContainsDuplicateSnapshotIdentity(PublishedProjectSnapshotDetails snapshot) =>
        snapshot.LayoutIds.Distinct(StringComparer.Ordinal).Count() != snapshot.LayoutIds.Count
        || snapshot.BlockVersionIds.Distinct(StringComparer.Ordinal).Count() != snapshot.BlockVersionIds.Count
        || snapshot.CapabilityBindings.Distinct().Count() != snapshot.CapabilityBindings.Count
        || snapshot.TargetReferences.Distinct().Count() != snapshot.TargetReferences.Count;

    private static bool ContainsDuplicateReleaseIdentity(ProjectReleaseSourceMetadata metadata) =>
        metadata.LayoutIds.Distinct(StringComparer.Ordinal).Count() != metadata.LayoutIds.Count
        || metadata.BlockVersionIds.Distinct(StringComparer.Ordinal).Count() != metadata.BlockVersionIds.Count
        || metadata.CapabilityBindings.Distinct().Count() != metadata.CapabilityBindings.Count
        || metadata.TargetReferences.Distinct().Count() != metadata.TargetReferences.Count
        || metadata.ProductionLine.LineControllerAuthorizations
            .Select(authorization => authorization.AuthorizationId)
            .Distinct(StringComparer.Ordinal)
            .Count() != metadata.ProductionLine.LineControllerAuthorizations.Count
        || metadata.ProductionLine.LineControllerAuthorizations
            .GroupBy(authorization => (authorization.OperationId, authorization.ActionId))
            .Any(group => group.Count() != 1);

    private static Result<VerifiedProjectRelease> Failure(ApplicationError error) =>
        Result.Failure<VerifiedProjectRelease>(error);
}
