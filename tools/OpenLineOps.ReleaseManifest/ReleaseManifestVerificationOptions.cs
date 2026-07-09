namespace OpenLineOps.ReleaseManifest;

public sealed record ReleaseManifestVerificationOptions(
    string ArtifactsDirectory,
    string ManifestPath,
    string? ChecksumsPath,
    IReadOnlyList<string> RequiredArtifactKinds);
