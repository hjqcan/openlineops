namespace OpenLineOps.Traceability.Api.Models;

public sealed record StoredTraceArtifactResponse(
    string StorageKey,
    string FileName,
    string? MediaType,
    long SizeBytes,
    string Sha256);
