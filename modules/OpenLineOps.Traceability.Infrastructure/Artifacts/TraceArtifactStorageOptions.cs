namespace OpenLineOps.Traceability.Infrastructure.Artifacts;

public sealed class TraceArtifactStorageOptions
{
    public const string SectionName = "OpenLineOps:Traceability:ArtifactStorage";

    public string Provider { get; set; } = TraceArtifactStorageProviders.FileSystem;

    public string RootPath { get; set; } = "data/openlineops-traceability-artifacts";

    public string ResolveRootPath()
    {
        return IsCanonical(RootPath)
            ? Path.GetFullPath(RootPath)
            : throw new InvalidOperationException(
                "Traceability artifact RootPath must be a non-empty canonical path.");
    }

    private static bool IsCanonical(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !char.IsWhiteSpace(value[0])
            && !char.IsWhiteSpace(value[^1]);
    }
}

public static class TraceArtifactStorageProviders
{
    public const string FileSystem = "FileSystem";

    public static TraceArtifactStorageProvider Parse(string? provider)
    {
        if (string.Equals(provider, FileSystem, StringComparison.Ordinal))
        {
            return TraceArtifactStorageProvider.FileSystem;
        }

        throw new InvalidOperationException(
            $"Unsupported traceability artifact storage provider '{provider}'. "
            + $"Expected exactly '{FileSystem}'.");
    }
}

public enum TraceArtifactStorageProvider
{
    FileSystem
}
