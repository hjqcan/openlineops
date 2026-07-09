namespace OpenLineOps.Traceability.Infrastructure.Artifacts;

public sealed class TraceArtifactStorageOptions
{
    public const string SectionName = "OpenLineOps:Traceability:ArtifactStorage";

    public string Provider { get; set; } = TraceArtifactStorageProviders.LocalFile;

    public string RootPath { get; set; } = "data/openlineops-traceability-artifacts";

    public string ResolveRootPath()
    {
        return string.IsNullOrWhiteSpace(RootPath)
            ? Path.GetFullPath("data/openlineops-traceability-artifacts")
            : Path.GetFullPath(RootPath.Trim());
    }
}

public static class TraceArtifactStorageProviders
{
    public const string LocalFile = "LocalFile";
}
