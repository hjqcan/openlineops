namespace OpenLineOps.Traceability.Api.Models;

public sealed record StationArtifactReceiptResponse(
    string ReceiptId,
    string AgentId,
    string StationId,
    Guid JobId,
    string ArtifactName,
    string ArtifactKind,
    string? MediaType,
    long SizeBytes,
    string Sha256,
    string StorageKey);
