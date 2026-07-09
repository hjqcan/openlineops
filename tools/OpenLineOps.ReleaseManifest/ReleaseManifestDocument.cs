namespace OpenLineOps.ReleaseManifest;

public sealed record ReleaseManifestDocument(
    int SchemaVersion,
    string Product,
    string Version,
    string GeneratedAtUtc,
    string? Commit,
    IReadOnlyList<ReleaseArtifactEntry> Artifacts);

public sealed record ReleaseArtifactEntry(
    string RelativePath,
    string FileName,
    string Kind,
    long SizeBytes,
    string Sha256);
