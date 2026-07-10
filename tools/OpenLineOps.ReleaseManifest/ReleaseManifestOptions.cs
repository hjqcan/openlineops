namespace OpenLineOps.ReleaseManifest;

public sealed record ReleaseManifestOptions(
    string Version,
    string ArtifactsDirectory,
    string ManifestPath,
    string ChecksumsPath,
    string? NotesPath,
    string? Commit,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<string> RequiredArtifactKinds);
