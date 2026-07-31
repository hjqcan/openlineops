using OpenLineOps.Application.Abstractions.Results;

namespace OpenLineOps.Traceability.Application.Artifacts;

public interface ITraceArtifactStorage
{
    Task<Result<StoredTraceArtifact>> StoreAsync(
        StoreTraceArtifactRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<TraceArtifactContent>> OpenReadAsync(
        string storageKey,
        CancellationToken cancellationToken = default);
}

public sealed record StoreTraceArtifactRequest(
    string? StorageKey,
    string FileName,
    string? MediaType,
    Stream Content,
    string? ExpectedSha256 = null,
    long? ExpectedSizeBytes = null);

public sealed record StoredTraceArtifact(
    string StorageKey,
    string FileName,
    string? MediaType,
    long SizeBytes,
    string Sha256);

public sealed record TraceArtifactContent(
    string StorageKey,
    string FileName,
    string? MediaType,
    long SizeBytes,
    string Sha256,
    Stream Content);
