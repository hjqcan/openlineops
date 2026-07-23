namespace OpenLineOps.Traceability.Application.Artifacts;

public interface IStationArtifactUploadAuthorizer
{
    ValueTask<StationArtifactUploadAuthorization> AuthorizeAsync(
        StationArtifactUploadAuthorizationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record StationArtifactUploadAuthorizationRequest(
    string AgentId,
    string StationId,
    Guid JobId,
    string ArtifactName,
    string ArtifactKind,
    string? MediaType,
    long SizeBytes,
    string Sha256);

public sealed record StationArtifactUploadAuthorization(
    StationArtifactUploadAuthorizationStatus Status,
    string FailureCode)
{
    public static StationArtifactUploadAuthorization Authorized { get; } = new(
        StationArtifactUploadAuthorizationStatus.Authorized,
        string.Empty);

    public static StationArtifactUploadAuthorization Reject(
        StationArtifactUploadAuthorizationStatus status,
        string failureCode)
    {
        if (status == StationArtifactUploadAuthorizationStatus.Authorized)
        {
            throw new ArgumentException(
                "An authorized Station artifact upload cannot carry a failure code.",
                nameof(status));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        return new StationArtifactUploadAuthorization(status, failureCode);
    }
}

public enum StationArtifactUploadAuthorizationStatus
{
    Authorized,
    MetadataInvalid,
    JobNotFound,
    IdentityForbidden,
    TerminalConflict
}
