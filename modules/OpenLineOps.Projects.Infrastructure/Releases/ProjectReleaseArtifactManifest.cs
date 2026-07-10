using OpenLineOps.Projects.Application.Releases;

namespace OpenLineOps.Projects.Infrastructure.Releases;

internal sealed record ProjectReleaseArtifactManifest(
    string Schema,
    int SchemaVersion,
    string SnapshotId,
    string ProjectId,
    string ApplicationId,
    DateTimeOffset PublishedAtUtc,
    string SourceApplicationRelativePath,
    string ApplicationProjectRelativePath,
    ProjectReleaseSourceMetadata Metadata,
    ProjectReleaseSourceFile[] Files,
    string ContentSha256)
{
    public const string CurrentSchema = "openlineops.project-release-artifact";

    public const int CurrentSchemaVersion = 3;

    public const string FileName = "release.json";
}
